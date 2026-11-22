using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Layal.SpeechLanguage;

namespace Layal.Services;

/// <summary>
/// Adapts local Whisper recognition and VoiceTut Voice Design synthesis.
/// Speech-language classification selects the TTS locale; no cloud fallback is introduced.
/// </summary>
public sealed class LocalSpeechLanguageProvider : ISpeechRecognitionProvider, ISpeechSynthesisProvider
{
    private readonly LocalVoiceService _voiceService;
    private readonly Func<VoiceTutGenerationSettings>? _settingsFactory;
    private readonly SpeechLanguageEngine _languageEngine;

    public LocalSpeechLanguageProvider(
        LocalVoiceService voiceService,
        Func<VoiceTutGenerationSettings>? settingsFactory = null,
        SpeechLanguageEngine? languageEngine = null)
    {
        _voiceService = voiceService ?? throw new ArgumentNullException(nameof(voiceService));
        _settingsFactory = settingsFactory;
        _languageEngine = languageEngine ?? SpeechLanguageEngine.CreateDefault();
    }

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
        CancellationToken cancellationToken)
    {
        var languageCode = SelectLanguageCode(_languageEngine.Analyze(text));
        return _voiceService.SpeakAsync(
            text,
            cancellationToken,
            options.OutputDeviceId,
            options.DeliveryStyleId,
            _settingsFactory?.Invoke() ?? new VoiceTutGenerationSettings(
                options.VoiceDesignInstruction ?? VoiceTutLocalService.DefaultVoiceDesign,
                options.QualitySteps,
                options.GuidanceScale,
                options.Speed,
                options.NormalizeText),
            languageCode);
    }

    /// <summary>
    /// Maps Layal's text-language classifications to VoiceTut language identifiers.
    /// Ambiguous Arabic uses Egyptian, Layal's primary Arabic locale, rather than assuming MSA.
    /// </summary>
    public static string SelectLanguageCode(SpeechLanguageAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.Language.PrimaryLanguage switch
        {
            SpeechLanguageKind.English => "en",
            SpeechLanguageKind.ArabicStandard => "ar",
            SpeechLanguageKind.EgyptianArabic => "arz",
            SpeechLanguageKind.Mixed => SelectMixedLanguageCode(analysis),
            SpeechLanguageKind.ArabicUnspecified => "arz",
            _ => "en"
        };
    }

    private static string SelectMixedLanguageCode(SpeechLanguageAnalysis analysis)
    {
        var arabicSegments = analysis.Language.Segments
            .Where(segment => segment.Language is SpeechLanguageKind.ArabicStandard
                or SpeechLanguageKind.EgyptianArabic
                or SpeechLanguageKind.ArabicUnspecified)
            .ToArray();

        if (arabicSegments.Any(segment => segment.Language == SpeechLanguageKind.EgyptianArabic))
            return "arz";
        if (arabicSegments.Length > 0 &&
            arabicSegments.All(segment => segment.Language == SpeechLanguageKind.ArabicStandard))
            return "ar";

        return "arz";
    }
}
