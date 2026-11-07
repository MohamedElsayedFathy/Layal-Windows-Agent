namespace Layal.Services;

/// <summary>Controls which of Layal's existing character artworks are shown in the floating mascot.</summary>
public static class CharacterDisplayModePolicy
{
    public const string DefaultMode = "duo";

    public static string Normalize(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "layal" => "layal",
            "robot" => "robot",
            "duo" => "duo",
            _ => DefaultMode
        };

    public static bool ShowsLayal(string? mode) => Normalize(mode) is "layal" or "duo";
    public static bool ShowsRobot(string? mode) => Normalize(mode) is "robot" or "duo";

    public static string GetAutomationName(string? mode, bool arabic) =>
        Normalize(mode) switch
        {
            "layal" => arabic ? "شخصية ليال" : "Layal character",
            "robot" => arabic ? "مساعد ليال الروبوت" : "Layal's robot assistant",
            _ => arabic ? "ليال ومساعدها الروبوت" : "Layal and her robot assistant"
        };

    public static string GetModeName(string? mode, bool arabic) =>
        Normalize(mode) switch
        {
            "layal" => arabic ? "ليال فقط" : "Layal only",
            "robot" => arabic ? "الروبوت فقط" : "Robot only",
            _ => arabic ? "ليال والروبوت" : "Layal + robot"
        };

    public static string GetHelpText(string? mode, bool arabic) =>
        Normalize(mode) switch
        {
            "layal" => arabic ? "يعرض رسومات ليال وتعبيراتها فقط، مع استمرار حالات المساعد في الخلفية." : "Shows Layal's portrait and expressions only; assistant status continues to update.",
            "robot" => arabic ? "يعرض مساعد ليال الروبوت وتعبيرات حالته فقط." : "Shows the robot assistant and its status expressions only.",
            _ => arabic ? "يعرض ليال ومساعدها الروبوت معًا." : "Shows Layal and her robot assistant together."
        };
}
