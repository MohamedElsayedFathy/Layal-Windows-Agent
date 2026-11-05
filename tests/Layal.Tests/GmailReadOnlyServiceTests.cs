using System.Linq;
using Layal.Models;
using System.Threading.Tasks;
using System.Threading;
using System.Text.Json;
using System.Net.Http;
using System.Net;
using System.IO;
using System.Collections.Generic;
using System;
using System.Security.Cryptography;
using System.Text;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class GmailReadOnlyServiceTests
{
    [TestMethod]
    public void OAuthClientIdMustBeAnExpectedGoogleDesktopClientId()
    {
        const string clientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com";
        Assert.AreEqual(clientId, GmailReadOnlyService.ValidateOAuthClientId(clientId));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GmailReadOnlyService.ValidateOAuthClientId("not-a-client-id"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GmailReadOnlyService.ValidateOAuthClientId("paste-your-client-secret-here"));
    }

    [TestMethod]
    public void PkceVerifierIsRandomAndChallengeUsesSha256Base64Url()
    {
        var first = GmailReadOnlyService.CreatePkceVerifier();
        var second = GmailReadOnlyService.CreatePkceVerifier();

        Assert.AreEqual(43, first.Length);
        Assert.AreEqual(43, second.Length);
        Assert.AreNotEqual(first, second);
        StringAssert.Matches(first, new System.Text.RegularExpressions.Regex("^[A-Za-z0-9_-]+$"));

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(first)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual(expected, GmailReadOnlyService.CreatePkceChallenge(first));
        Assert.AreNotEqual(first, GmailReadOnlyService.CreatePkceChallenge(first));
        Assert.ThrowsExactly<ArgumentException>(() => GmailReadOnlyService.CreatePkceChallenge("short"));
    }

    [TestMethod]
    public void HtmlBodyIsConvertedToPlainTextWithoutExecutableOrRemoteContent()
    {
        var converted = GmailReadOnlyService.ConvertHtmlToPlainText(
            "<html><head><style>.secret{display:none}</style></head><body>" +
            "<h1>Hello &amp; welcome</h1><script>stealCredentials()</script>" +
            "<iframe src='https://tracker.example'></iframe><form>Send password</form>" +
            "<p>Second line</p></body></html>");

        StringAssert.Contains(converted, "Hello & welcome");
        StringAssert.Contains(converted, "Second line");
        Assert.IsFalse(converted.Contains("stealCredentials", StringComparison.Ordinal));
        Assert.IsFalse(converted.Contains("tracker.example", StringComparison.Ordinal));
        Assert.IsFalse(converted.Contains("Send password", StringComparison.Ordinal));
        Assert.IsFalse(converted.Contains(".secret", StringComparison.Ordinal));
        Assert.IsTrue(converted.Length <= GmailReadOnlyService.MaximumBodyCharacters);
    }

    [TestMethod]
    public void AttachmentPreviewAllowsOnlyBoundedTextFormatsAndSanitizesNames()
    {
        Assert.IsTrue(GmailReadOnlyService.IsSupportedTextAttachment("notes.txt", "text/plain"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedTextAttachment("table.csv", "text/csv"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedTextAttachment("page.html", "text/html"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedTextAttachment("payload.exe", "application/octet-stream"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedTextAttachment("payload.exe", "text/plain"),
            "A sender-controlled MIME type must not override a non-text filename extension.");
        Assert.IsFalse(GmailReadOnlyService.IsSupportedTextAttachment("archive.zip", "application/zip"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedDocumentAttachment("report.pdf", "application/pdf"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedDocumentAttachment("report.docx", "application/octet-stream"),
            "The allow-listed filename extension is authoritative when MIME metadata is sender-controlled.");
        Assert.IsTrue(GmailReadOnlyService.IsSupportedDocumentAttachment("budget.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedImageAttachment("receipt.png"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedImageAttachment("scan.tiff"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedImageAttachment("payload.webp"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedImageAttachment("payload.exe"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedDocumentAttachment("payload.exe", "application/pdf"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedDocumentAttachment("archive.zip", "application/zip"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedTextAttachment(string.Empty, "text/plain"),
            "An extensionless attachment may be previewed only when its MIME type is an approved text type.");

        var safe = GmailReadOnlyService.SanitizeAttachmentFileName(@"C:\\private\\..\\report.txt");
        Assert.IsFalse(safe.Contains('\\'));
        Assert.IsFalse(safe.Contains('/'));
        Assert.IsFalse(safe.Contains(':'));
        Assert.IsTrue(safe.EndsWith("report.txt", StringComparison.Ordinal));
        Assert.IsTrue(safe.Length <= 120);
    }

    [TestMethod]
    public async Task ReadMessageAsyncConvertsHtmlAndPreviewsOnlySmallTextAttachments()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The Gmail credential store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var credentialPath = System.IO.Path.Combine(temp.Path, "gmail.dpapi");
        new ProtectedJsonStore<GmailOAuthCredential>(credentialPath).Save(new GmailOAuthCredential
        {
            ClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            RefreshToken = "test-refresh-token"
        });

        var inlineText = "Inline attachment text";
        var remoteText = "Remote markdown attachment";
        var longPreview = new string('p', GmailReadOnlyService.MaximumAttachmentPreviewCharacters / 3);
        var html = "<html><body><p>HTML only body</p><script>bad()</script><style>.x{}</style></body></html>";
        var message = JsonSerializer.Serialize(new
        {
            id = "message-1",
            threadId = "thread-1",
            internalDate = "1793700000000",
            payload = new
            {
                mimeType = "multipart/mixed",
                headers = new[]
                {
                    new { name = "From", value = "sender@example.com" },
                    new { name = "To", value = "recipient@example.com" },
                    new { name = "Subject", value = "HTML and attachments" }
                },
                parts = new object[]
                {
                    new
                    {
                        mimeType = "text/html",
                        filename = "",
                        body = new { data = Base64Url(html) }
                    },
                    new
                    {
                        mimeType = "text/plain",
                        filename = "notes.txt",
                        body = new { size = inlineText.Length, data = Base64Url(inlineText) }
                    },
                    new
                    {
                        mimeType = "text/markdown",
                        filename = "readme.md",
                        body = new { size = remoteText.Length, attachmentId = "attachment-1" }
                    },
                    new
                    {
                        mimeType = "text/plain",
                        filename = "one.txt",
                        body = new { size = longPreview.Length, data = Base64Url(longPreview) }
                    },
                    new
                    {
                        mimeType = "text/plain",
                        filename = "two.txt",
                        body = new { size = longPreview.Length, data = Base64Url(longPreview) }
                    },
                    new
                    {
                        mimeType = "text/plain",
                        filename = "three.txt",
                        body = new { size = longPreview.Length, data = Base64Url(longPreview) }
                    },
                    new
                    {
                        mimeType = "text/plain",
                        filename = "four.txt",
                        body = new { size = longPreview.Length, data = Base64Url(longPreview) }
                    },
                    new
                    {
                        mimeType = "application/octet-stream",
                        filename = "payload.exe",
                        body = new { size = 32, attachmentId = "must-not-download" }
                    },
                    new
                    {
                        mimeType = "text/csv",
                        filename = "large.csv",
                        body = new { size = GmailReadOnlyService.MaximumAttachmentBytes + 1, attachmentId = "too-large" }
                    }
                }
            }
        });

        using var handler = new GmailApiStubHandler(message, remoteText);
        using var client = new HttpClient(handler);
        var service = new GmailReadOnlyService(credentialPath, client);

        var detail = await service.ReadMessageAsync("message-1");

        StringAssert.Contains(detail.PlainTextBody, "HTML only body");
        Assert.IsFalse(detail.PlainTextBody.Contains("bad()", StringComparison.Ordinal));
        Assert.IsFalse(detail.PlainTextBody.Contains(".x{", StringComparison.Ordinal));
        Assert.AreEqual(8, detail.Attachments!.Count);
        Assert.AreEqual(inlineText, detail.Attachments[0].PreviewText);
        Assert.AreEqual(remoteText, detail.Attachments[1].PreviewText);
        StringAssert.Contains(detail.Attachments[0].Status, "available");
        StringAssert.Contains(detail.Attachments[5].Status, "preview budget");
        StringAssert.Contains(detail.Attachments[6].Status, "Not previewed");
        StringAssert.Contains(detail.Attachments[7].Status, "Not previewed");
        Assert.IsTrue(detail.Attachments.Sum(item => item.PreviewText.Length) <=
            GmailReadOnlyService.MaximumAttachmentPreviewCharacters,
            "Total text previews across all message attachments must stay within the aggregate budget.");
        Assert.IsTrue(detail.Attachments.Take(5).All(item =>
            item.PreviewText.Length <= GmailReadOnlyService.MaximumAttachmentPreviewCharacters));
        Assert.IsTrue(detail.Attachments.Single(item => item.FileName == "four.txt").PreviewText.Length < longPreview.Length,
            "The final text attachment should be truncated when the aggregate preview budget is exhausted.");
        Assert.AreEqual(1, handler.AttachmentRequests);
        Assert.IsFalse(handler.RequestedPaths.Any(path => path.Contains("must-not-download", StringComparison.Ordinal)));
        Assert.IsFalse(handler.RequestedPaths.Any(path => path.Contains("too-large", StringComparison.Ordinal)));
    }


    [TestMethod]
    public async Task ReadMessageAsyncExtractsDocxAttachmentLocallyWithoutSavingItToWorkspace()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The Gmail credential store uses Windows DPAPI and document preview uses Windows APIs.");

        using var temp = new TestTempDirectory();
        var credentialPath = Path.Combine(temp.Path, "gmail.dpapi");
        new ProtectedJsonStore<GmailOAuthCredential>(credentialPath).Save(new GmailOAuthCredential
        {
            ClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            RefreshToken = "test-refresh-token"
        });

        var existingTempPreviews = Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        byte[] docxBytes;
        using (var package = new MemoryStream())
        {
            using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                       package,
                       DocumentFormat.OpenXml.WordprocessingDocumentType.Document,
                       true))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body(
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("Gmail document preview sentinel")))));
                main.Document.Save();
            }
            docxBytes = package.ToArray();
        }

        var messageJson = JsonSerializer.Serialize(new
        {
            id = "message-docx",
            threadId = "thread-docx",
            snippet = "Document attachment",
            internalDate = "1790000000000",
            payload = new
            {
                headers = new[]
                {
                    new { name = "From", value = "sender@example.com" },
                    new { name = "Subject", value = "Document preview" }
                },
                parts = new[]
                {
                    new
                    {
                        filename = "report.docx",
                        mimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        body = new { size = docxBytes.Length, data = Base64Url(docxBytes) }
                    }
                }
            }
        });

        using var http = new HttpClient(new GmailInlineAttachmentHandler(messageJson));
        var service = new GmailReadOnlyService(credentialPath, http);
        var detail = await service.ReadMessageAsync("message-docx");

        Assert.AreEqual(1, detail.Attachments!.Count);
        StringAssert.Contains(detail.Attachments[0].Status, "Local document text preview available");
        StringAssert.Contains(detail.Attachments[0].PreviewText, "Gmail document preview sentinel");
        var leakedPreviews = Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .Where(path => !existingTempPreviews.Contains(path)).ToArray();
        Assert.AreEqual(0, leakedPreviews.Length,
            "Gmail document bytes must not be retained after the local preview is extracted.");
    }


    [TestMethod]
    public async Task ReadMessageAsyncOcrsSmallImageAttachmentsLocallyAndDeletesTemporaryFile()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The Gmail credential store uses Windows DPAPI and OCR uses bundled Windows models.");

        using var temp = new TestTempDirectory();
        var credentialPath = Path.Combine(temp.Path, "gmail.dpapi");
        new ProtectedJsonStore<GmailOAuthCredential>(credentialPath).Save(new GmailOAuthCredential
        {
            ClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            RefreshToken = "test-refresh-token"
        });

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "arabic-ocr-fixture.png");
        var imageBytes = await File.ReadAllBytesAsync(fixturePath);
        Assert.IsTrue(imageBytes.Length <= GmailReadOnlyService.MaximumAttachmentBytes,
            "The OCR fixture must remain within Gmail's 1 MiB attachment preview limit.");
        var existingPreviews = Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var messageJson = JsonSerializer.Serialize(new
        {
            id = "message-image",
            threadId = "thread-image",
            snippet = "Image attachment",
            internalDate = "1790000000000",
            payload = new
            {
                headers = new[]
                {
                    new { name = "From", value = "sender@example.com" },
                    new { name = "Subject", value = "Image OCR preview" }
                },
                parts = new[]
                {
                    new
                    {
                        filename = "receipt.png",
                        mimeType = "image/png",
                        body = new { size = imageBytes.Length, data = Base64Url(imageBytes) }
                    }
                }
            }
        });

        using var http = new HttpClient(new GmailInlineAttachmentHandler(messageJson, "message-image"));
        var service = new GmailReadOnlyService(credentialPath, http);
        var detail = await service.ReadMessageAsync("message-image");

        Assert.AreEqual(1, detail.Attachments!.Count);
        StringAssert.Contains(detail.Attachments[0].Status, "Local image OCR preview available");
        StringAssert.Contains(detail.Attachments[0].PreviewText, "اختبار");
        Assert.IsTrue(detail.Attachments[0].PreviewText.Length <= GmailReadOnlyService.MaximumSingleAttachmentPreviewCharacters);
        var leakedPreviews = Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .Where(path => !existingPreviews.Contains(path)).ToArray();
        Assert.AreEqual(0, leakedPreviews.Length,
            "Image attachment bytes may be processed in a private temporary file but the file must be removed immediately.");
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');


    private sealed class GmailInlineAttachmentHandler(string messageJson, string messageId = "message-docx") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
            if (uri.Host == "oauth2.googleapis.com" && uri.AbsolutePath == "/token")
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    access_token = "test-access-token",
                    expires_in = 3600
                })));

            if (uri.Host == "gmail.googleapis.com" &&
                uri.AbsolutePath.EndsWith("/messages/" + Uri.EscapeDataString(messageId), StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(messageJson));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed class GmailApiStubHandler(string fullMessageJson, string remoteAttachmentText) : HttpMessageHandler
    {
        public int AttachmentRequests { get; private set; }
        public List<string> RequestedPaths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            RequestedPaths.Add(uri.AbsolutePath);

            if (uri.AbsoluteUri == "https://oauth2.googleapis.com/token")
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                return JsonResponse("{\"access_token\":\"test-access-token\",\"expires_in\":3600}");
            }

            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("test-access-token", request.Headers.Authorization?.Parameter);

            if (uri.AbsolutePath.EndsWith("/messages/message-1", StringComparison.Ordinal))
                return JsonResponse(fullMessageJson);

            if (uri.AbsolutePath.EndsWith("/attachments/attachment-1", StringComparison.Ordinal))
            {
                AttachmentRequests++;
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    size = remoteAttachmentText.Length,
                    data = Base64Url(remoteAttachmentText)
                }));
            }

            throw new InvalidOperationException("The Gmail service made an unexpected network request: " + uri.AbsoluteUri);
        }

        private static Task<HttpResponseMessage> JsonResponse(string content) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }

    [TestMethod]
    public void MessageBodyAndResultsAreBoundedByServiceContracts()
    {
        Assert.AreEqual(25, GmailReadOnlyService.MaximumListedMessages);
        Assert.AreEqual(15_000, GmailReadOnlyService.MaximumBodyCharacters);
    }
}
