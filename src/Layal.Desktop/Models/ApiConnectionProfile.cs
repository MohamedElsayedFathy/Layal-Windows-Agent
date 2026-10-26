namespace Layal.Models;

/// <summary>
/// A user-configured REST/JSON API connection. Secret values are persisted only inside DPAPI-protected storage.
/// </summary>
public sealed class ApiConnectionProfile
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>Optional local image path copied into Layal's app-data folder.</summary>
    public string ImagePath { get; set; } = string.Empty;
    /// <summary>Optional model identifier when this profile is used as Layal's cloud AI provider.</summary>
    public string ModelId { get; set; } = string.Empty;
    /// <summary>Marks the single OpenAI-compatible cloud provider used by Layal when local AI is disabled.</summary>
    public bool IsLayalProvider { get; set; }
    /// <summary>None, Bearer, or CustomHeader.</summary>
    public string AuthenticationType { get; set; } = "Bearer";
    public string AuthHeaderName { get; set; } = "X-API-Key";
    public string AuthSecret { get; set; } = string.Empty;
    /// <summary>Optional non-secret HTTP headers as a JSON object of string values.</summary>
    public string AdditionalHeadersJson { get; set; } = string.Empty;
    /// <summary>Optional user-provided API usage notes; do not include secrets.</summary>
    public string Instructions { get; set; } = string.Empty;
    /// <summary>Relative path used by the manual connection test.</summary>
    public string TestPath { get; set; } = string.Empty;
}
