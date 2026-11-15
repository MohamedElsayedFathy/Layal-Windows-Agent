using System;
using System.Threading;
using System.Threading.Tasks;
using Layal.SpeechLanguage;

namespace Layal.Services;

/// <summary>
/// Adapts local Whisper recognition and the isolated VoiceTut Voice Design synthesizer.
/// Audio capture consent remains in the WPF host; no cloud speech fallback is introduced.
/// </summary>
public sealed class LocalSpeechLanguageProvider : ISpeechRecognitionProvider, ISpeechSynthesisProvider
{
    private readonly LocalVoiceService _voiceService;

    public LocalSpeechLanguageProvider(LocalVoiceService voiceService) =>
        _voiceService = voiceService ?? throw new ArgumentNullException(nameof(voiceService));

    public async Task<string> RecognizeAsync(ReadOnlyMemory<byte> waveAudio, CancellationToken cancellationToken)
    {
        var audioCopy = waveAudio.ToArray();
        try
        {
            return await _voiceService.TranscribeAsync(audioCopy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(audioCopy, 0, audioCopy.Length);
        }
    }

    public Task SynthesizeAndPlayAsync(
        string text,
        SpeechSynthesisOptions options,
        CancellationToken cancellationToken) =>
        _voiceService.SpeakAsync(
            text,
            cancellationToken,
            options.OutputDeviceId,
            options.DeliveryStyleId,
            new VoiceTutGenerationSettings(
                options.VoiceDesignInstruction ?? VoiceTutLocalService.DefaultVoiceDesign,
                options.QualitySteps,
                options.GuidanceScale,
                options.Speed,
                options.NormalizeText));
}
