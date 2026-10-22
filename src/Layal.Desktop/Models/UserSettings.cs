namespace Layal.Models;

public sealed class UserSettings
{
    public string Language { get; set; } = "ar";
    public string ThemeMode { get; set; } = "light";
    public string ProviderBaseUrl { get; set; } = "http://127.0.0.1:31415/v1";
    public string ModelId { get; set; } = "auto";
    public bool MascotAlwaysOnTop { get; set; } = true;
    public bool MascotAnimationsEnabled { get; set; } = true;
    public double MascotScale { get; set; } = 1.0;
    public double MascotOpacity { get; set; } = 1.0;
    public double MascotLeft { get; set; } = -1;
    public double MascotTop { get; set; } = -1;
    public bool AllowWindowTitleInspection { get; set; } = false;
}
