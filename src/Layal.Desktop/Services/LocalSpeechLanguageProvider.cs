using System;
using System.Threading;
using System.Threading.Tasks;
using Layal.SpeechLanguage;

namespace Layal.Services;

/// <summary>
/// Adapter over Layal's integrity-checked local Whisper/Piper service.
/// No cloud fallback is introduced, and microphone consent remains in the WPF host.
/// </summary>
public sealed class LocalSpeechLanguageProvider : ISpeechRecognitionProvider, ISpeechSynthesisProvider
{
    private readonly LocalVoiceService _voiceService;

    public LocalSpeechLanguageProvider(LocalVoiceService voiceService) =>
        _voiceService = voiceService ?? throw new ArgumentNullException(nameof(voiceService));

    public Task<string> RecognizeAsync(ReadOnlyMemory<byte> waveAudio, CancellationToken cancellationToken) =>
        _voiceService.TranscribeAsync(waveAudio.ToArray(), cancellationToken);

    public Task SynthesizeAndPlayAsync(
        string text,
        SpeechSynthesisOptions options,
        CancellationToken cancellationToken) =>
        _voiceService.SpeakAsync(
            text,
            cancellationToken,
            options.OutputDeviceId,
            options.DeliveryStyleId);
}
