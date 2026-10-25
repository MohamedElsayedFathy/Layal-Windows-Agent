using System.Net;
using System.Text;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class SpeechToTextServiceTests
{
    [TestMethod]
    public async Task TranscribeAsync_PostsWavWithArabicEnglishAndEgyptianDialectGuidance()
    {
        Uri? requestedUri = null;
        string? bearerToken = null;
        string? multipartBody = null;
        string? contentType = null;
        using var client = new HttpClient(new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestedUri = request.RequestUri;
            bearerToken = request.Headers.Authorization?.Parameter;
            contentType = request.Content?.Headers.ContentType?.MediaType;
            multipartBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"إزيك، how are you?\"}", Encoding.UTF8, "application/json")
            };
        }));
        using var service = new SpeechToTextService(() => client);

        var transcript = await service.TranscribeAsync(
            "https://speech.example/v1", "test-key", "gpt-transcribe", MakeTinyWav());

        Assert.AreEqual("إزيك، how are you?", transcript);
        Assert.AreEqual("https://speech.example/v1/audio/transcriptions", requestedUri?.AbsoluteUri);
        Assert.AreEqual("test-key", bearerToken);
        Assert.AreEqual("multipart/form-data", contentType);
        Assert.IsNotNull(multipartBody);
        StringAssert.Contains(multipartBody, "name=file; filename=recording.wav");
        StringAssert.Contains(multipartBody, "name=model");
        StringAssert.Contains(multipartBody, "gpt-transcribe");
        StringAssert.Contains(multipartBody, "name=\"languages[]\"");
        StringAssert.Contains(multipartBody, "name=prompt");
        StringAssert.Contains(multipartBody, "العامية المصرية");
        StringAssert.Contains(multipartBody, "ar");
        StringAssert.Contains(multipartBody, "en");
    }

    [TestMethod]
    public async Task TranscribeAsync_RejectsRemoteHttpBeforeSendingAudio()
    {
        var requestSent = false;
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"unexpected\"}", Encoding.UTF8, "application/json")
            });
        }));
        using var service = new SpeechToTextService(() => client);

        InvalidOperationException? error = null;
        try
        {
            await service.TranscribeAsync("http://speech.example/v1", "test-key", "gpt-transcribe", MakeTinyWav());
        }
        catch (InvalidOperationException ex)
        {
            error = ex;
        }

        Assert.IsNotNull(error);
        StringAssert.Contains(error?.Message ?? string.Empty, "HTTPS");
        Assert.IsFalse(requestSent, "Audio must not be sent to a remote provider over unencrypted HTTP.");
    }

    [TestMethod]
    public async Task TranscribeAsync_AllowsLocalLoopbackHttpProvider()
    {
        Uri? requestedUri = null;
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"hello\"}", Encoding.UTF8, "application/json")
            });
        }));
        using var service = new SpeechToTextService(() => client);

        var transcript = await service.TranscribeAsync(
            "http://127.0.0.1:31415/v1", "local-test-key", "x", MakeTinyWav());

        Assert.AreEqual("hello", transcript);
        Assert.AreEqual("http://127.0.0.1:31415/v1/audio/transcriptions", requestedUri?.AbsoluteUri);
    }

    [TestMethod]
    public void BuildTranscriptionUri_RejectsCredentialsAndQueryStrings()
    {
        var rejectedCredentials = false;
        try { _ = SpeechToTextService.BuildTranscriptionUri("https://user:password@speech.example/v1"); }
        catch (InvalidOperationException) { rejectedCredentials = true; }
        Assert.IsTrue(rejectedCredentials);

        var rejectedQuery = false;
        try { _ = SpeechToTextService.BuildTranscriptionUri("https://speech.example/v1?token=secret"); }
        catch (InvalidOperationException) { rejectedQuery = true; }
        Assert.IsTrue(rejectedQuery);
    }

    private static byte[] MakeTinyWav()
    {
        using var stream = new MemoryStream();
        using (var writer = new NAudio.Wave.WaveFileWriter(
                   stream, new NAudio.Wave.WaveFormat(16000, 16, 1)))
        {
            writer.Write(new byte[320], 0, 320);
        }
        return stream.ToArray();
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
