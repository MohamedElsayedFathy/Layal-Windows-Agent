using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

public sealed record CurrentWeatherReport(
    string LocationDisplay,
    double TemperatureC,
    double ApparentTemperatureC,
    int RelativeHumidityPercent,
    double WindSpeedKmh,
    int WeatherCode,
    DateTimeOffset FetchedAt);

public sealed class WeatherLocationNotFoundException : Exception
{
    public WeatherLocationNotFoundException(string location)
        : base($"No matching weather location was found for '{location}'.")
    {
    }
}

/// <summary>
/// Retrieves current weather for an explicitly selected city using Open-Meteo geocoding
/// and forecast endpoints. No device GPS/IP location is read by this service.
/// </summary>
public sealed class WeatherService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly HttpClient _httpClient;

    public WeatherService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<CurrentWeatherReport> GetCurrentWeatherAsync(
        string location,
        bool isArabic,
        CancellationToken cancellationToken = default)
    {
        var query = (location ?? string.Empty).Trim();
        if (query.Length == 0)
            throw new ArgumentException("A city name is required.", nameof(location));
        if (query.Length > 120)
            throw new ArgumentException("The city name must be 120 characters or fewer.", nameof(location));

        var language = isArabic ? "ar" : "en";
        var geocodingUri = new Uri(
            "https://geocoding-api.open-meteo.com/v1/search?name=" +
            Uri.EscapeDataString(query) +
            "&count=1&language=" + language + "&format=json");

        using var geocoding = await GetJsonAsync(geocodingUri, cancellationToken);
        if (!geocoding.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            throw new WeatherLocationNotFoundException(query);
        }

        var city = results[0];
        if (!city.TryGetProperty("latitude", out var latitudeElement) ||
            !latitudeElement.TryGetDouble(out var latitude) ||
            !city.TryGetProperty("longitude", out var longitudeElement) ||
            !longitudeElement.TryGetDouble(out var longitude))
        {
            throw new InvalidOperationException("The weather location response did not contain coordinates.");
        }

        var cityName = GetString(city, "name") ?? query;
        var country = GetString(city, "country");
        var locationDisplay = string.IsNullOrWhiteSpace(country) ? cityName : $"{cityName}, {country}";

        const string currentVariables = "temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m";
        var forecastUri = new Uri(
            "https://api.open-meteo.com/v1/forecast?latitude=" +
            latitude.ToString(CultureInfo.InvariantCulture) +
            "&longitude=" + longitude.ToString(CultureInfo.InvariantCulture) +
            "&current=" + Uri.EscapeDataString(currentVariables) +
            "&timezone=auto&temperature_unit=celsius&wind_speed_unit=kmh");

        using var forecast = await GetJsonAsync(forecastUri, cancellationToken);
        if (!forecast.RootElement.TryGetProperty("current", out var current))
            throw new InvalidOperationException("The weather response did not contain current conditions.");

        return new CurrentWeatherReport(
            locationDisplay,
            GetDouble(current, "temperature_2m"),
            GetDouble(current, "apparent_temperature"),
            GetInt(current, "relative_humidity_2m"),
            GetDouble(current, "wind_speed_10m"),
            GetInt(current, "weather_code"),
            DateTimeOffset.Now);
    }

    public static string GetConditionText(int weatherCode, bool isArabic) =>
        weatherCode switch
        {
            0 => isArabic ? "صحو" : "Clear sky",
            1 => isArabic ? "صحو غالبًا" : "Mainly clear",
            2 => isArabic ? "غائم جزئيًا" : "Partly cloudy",
            3 => isArabic ? "غائم" : "Overcast",
            45 => isArabic ? "ضباب" : "Fog",
            48 => isArabic ? "ضباب متجمد" : "Depositing rime fog",
            51 => isArabic ? "رذاذ خفيف" : "Light drizzle",
            53 => isArabic ? "رذاذ متوسط" : "Moderate drizzle",
            55 => isArabic ? "رذاذ كثيف" : "Dense drizzle",
            56 => isArabic ? "رذاذ متجمد خفيف" : "Light freezing drizzle",
            57 => isArabic ? "رذاذ متجمد كثيف" : "Dense freezing drizzle",
            61 => isArabic ? "أمطار خفيفة" : "Slight rain",
            63 => isArabic ? "أمطار متوسطة" : "Moderate rain",
            65 => isArabic ? "أمطار غزيرة" : "Heavy rain",
            66 => isArabic ? "مطر متجمد خفيف" : "Light freezing rain",
            67 => isArabic ? "مطر متجمد غزير" : "Heavy freezing rain",
            71 => isArabic ? "تساقط ثلوج خفيف" : "Slight snowfall",
            73 => isArabic ? "تساقط ثلوج متوسط" : "Moderate snowfall",
            75 => isArabic ? "تساقط ثلوج كثيف" : "Heavy snowfall",
            77 => isArabic ? "حبيبات ثلجية" : "Snow grains",
            80 => isArabic ? "زخات مطر خفيفة" : "Slight rain showers",
            81 => isArabic ? "زخات مطر متوسطة" : "Moderate rain showers",
            82 => isArabic ? "زخات مطر غزيرة" : "Violent rain showers",
            85 => isArabic ? "زخات ثلج خفيفة" : "Slight snow showers",
            86 => isArabic ? "زخات ثلج كثيفة" : "Heavy snow showers",
            95 => isArabic ? "عاصفة رعدية" : "Thunderstorm",
            96 => isArabic ? "عاصفة رعدية مع بَرَد" : "Thunderstorm with slight hail",
            99 => isArabic ? "عاصفة رعدية مع بَرَد كثيف" : "Thunderstorm with heavy hail",
            _ => isArabic ? "حالة طقس غير معروفة" : "Unknown weather conditions"
        };

    public static string GetConditionIcon(int weatherCode) =>
        weatherCode switch
        {
            0 => "☀️",
            1 or 2 => "🌤️",
            3 => "☁️",
            45 or 48 => "🌫️",
            51 or 53 or 55 or 56 or 57 or 61 or 63 or 66 => "🌦️",
            65 or 67 or 80 or 81 or 82 => "🌧️",
            71 or 73 or 75 or 77 or 85 or 86 => "❄️",
            95 or 96 or 99 => "⛈️",
            _ => "🌡️"
        };

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double GetDouble(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var result))
            return result;
        throw new InvalidOperationException($"The weather response did not contain '{propertyName}'.");
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result))
            return result;
        throw new InvalidOperationException($"The weather response did not contain '{propertyName}'.");
    }
}
