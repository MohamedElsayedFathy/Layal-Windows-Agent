using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Layal.Models;
using Layal.Services;
using Microsoft.Win32;

namespace Layal.Views;

public partial class MainWindow
{
    private void OpenNotepadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = SafeWindowsTools.LaunchNotepad();
            var displayResult = UiLocalization.FormatNotepadResult(result, IsArabic);
            SetAccessibleStatusText(HomeResult, displayResult);
            SetAccessibleStatusText(TaskResult, displayResult);
            AppRuntime.MascotWindow?.SetMood("success");
        }
        catch (Exception ex)
        {
            var message = UiLocalization.FormatTechnicalError("تعذر فتح المفكرة", "Could not launch Notepad", ex, IsArabic);
            SetAccessibleStatusText(HomeResult, message);
            SetAccessibleStatusText(TaskResult, message);
            AppRuntime.MascotWindow?.SetMood("error");
        }
    }

    private bool ApiConnectionMatchesSearch(object item)
    {
        if (item is not ApiConnectionProfile profile) return false;
        var query = ApiConnectionSearchInput?.Text?.Trim() ?? string.Empty;
        if (query.Length == 0) return true;
        return profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               profile.BaseUrl.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               profile.Instructions.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               profile.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ApiConnectionSearchInput_TextChanged(object sender, TextChangedEventArgs e) =>
        _apiConnectionsView?.Refresh();

    private void RefreshApiConnections(string? selectedName = null)
    {
        if (ApiConnectionList is null || AppRuntime.ApiConnectionService is null) return;
        ApiConnections.Clear();
        foreach (var profile in AppRuntime.ApiConnectionService.GetAll())
            ApiConnections.Add(profile);
        _apiConnectionsView?.Refresh();

        var selected = ApiConnections.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(selectedName) &&
            string.Equals(x.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        selected ??= ApiConnections.FirstOrDefault();
        ApiConnectionList.SelectedItem = selected;
        if (selected is null) ClearApiConnectionForm();
    }

    private void ClearApiConnectionForm()
    {
        _editingApiConnectionName = null;
        ApiConnectionNameInput.Text = string.Empty;
        ApiConnectionBaseUrlInput.Text = string.Empty;
        ApiConnectionModelIdInput.Text = string.Empty;
        ApiConnectionImagePathInput.Text = string.Empty;
        ApiAuthHeaderNameInput.Text = "X-API-Key";
        ApiAuthSecretInput.Clear();
        ApiAdditionalHeadersInput.Clear();
        ApiTestPathInput.Text = string.Empty;
        ApiInstructionsInput.Clear();
        ApiAuthTypeComboBox.SelectedIndex = 1;
        UpdateLayalApiButtonLabel();
    }

    private string SelectedApiAuthenticationType =>
        (ApiAuthTypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Bearer";

    private ApiConnectionProfile BuildApiConnectionProfile()
    {
        var name = ApiConnectionNameInput.Text.Trim();
        var existing = !string.IsNullOrWhiteSpace(_editingApiConnectionName)
            ? AppRuntime.ApiConnectionService.Find(_editingApiConnectionName)
            : null;
        var secret = ApiAuthSecretInput.Password;
        if (string.IsNullOrWhiteSpace(secret) && existing is not null)
            secret = existing.AuthSecret;

        return new ApiConnectionProfile
        {
            Name = name,
            BaseUrl = ApiConnectionBaseUrlInput.Text.Trim(),
            ImagePath = ApiConnectionImagePathInput.Text.Trim(),
            ModelId = ApiConnectionModelIdInput.Text.Trim(),
            IsLayalProvider = existing?.IsLayalProvider ?? false,
            AuthenticationType = SelectedApiAuthenticationType,
            AuthHeaderName = ApiAuthHeaderNameInput.Text.Trim(),
            AuthSecret = secret,
            AdditionalHeadersJson = ApiAdditionalHeadersInput.Text.Trim(),
            TestPath = ApiTestPathInput.Text.Trim(),
            Instructions = ApiInstructionsInput.Text.Trim()
        };
    }

    private void ApiConnectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApiConnectionList.SelectedItem is not ApiConnectionProfile profile)
        {
            _editingApiConnectionName = null;
            UpdateLayalApiButtonLabel();
            return;
        }

        _editingApiConnectionName = profile.Name;
        ApiConnectionNameInput.Text = profile.Name;
        ApiConnectionBaseUrlInput.Text = profile.BaseUrl;
        ApiConnectionModelIdInput.Text = profile.ModelId;
        ApiConnectionImagePathInput.Text = profile.ImagePath;
        ApiAuthHeaderNameInput.Text = profile.AuthHeaderName;
        ApiAuthSecretInput.Password = profile.AuthSecret;
        ApiAdditionalHeadersInput.Text = profile.AdditionalHeadersJson;
        ApiTestPathInput.Text = profile.TestPath;
        ApiInstructionsInput.Text = profile.Instructions;
        ApiAuthTypeComboBox.SelectedItem = ApiAuthTypeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), profile.AuthenticationType, StringComparison.Ordinal));
        UpdateLayalApiButtonLabel();
    }

    private void UpdateLayalApiButtonLabel()
    {
        if (UseAsLayalApiButton is null) return;
        var active = (ApiConnectionList?.SelectedItem as ApiConnectionProfile)?.IsLayalProvider == true;
        UseAsLayalApiButton.Content = active
            ? (IsArabic ? "API ليال الحالي" : "Current Layal API")
            : (IsArabic ? "استخدام هذا كـ API ليال" : "Use as Layal API");
    }

    private void NewApiConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        ApiConnectionList.SelectedItem = null;
        _editingApiConnectionName = null;
        ClearApiConnectionForm();
        ApiConnectionsStatus.Text = IsArabic
            ? "أدخل الاسم وعنوان API الأساسي، وهما إجباريان. بعد الإضافة ستُحفظ الوصلة في القائمة وتُفرغ الخانات."
            : "Enter the required name and API base URL. After adding, the connection is listed and the form is cleared.";
        ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
        ApiConnectionNameInput.Focus();
    }

    private static void ValidateLayalProviderProfile(ApiConnectionProfile profile, bool isArabic)
    {
        if (!string.Equals(profile.AuthenticationType, "Bearer", StringComparison.Ordinal))
            throw new InvalidOperationException(isArabic
                ? "API ليال السحابي يحتاج مصادقة Bearer ومفتاح API."
                : "Layal's cloud API provider requires Bearer authentication and an API key.");
        if (string.IsNullOrWhiteSpace(profile.AuthSecret))
            throw new InvalidOperationException(isArabic
                ? "أدخل مفتاح API في خانة المصادقة قبل تعيينه كـ API ليال."
                : "Enter an API key before selecting this as Layal's API.");
        if (string.IsNullOrWhiteSpace(profile.ModelId))
            throw new InvalidOperationException(isArabic
                ? "أدخل معرّف النموذج قبل تعيين الاتصال كـ API ليال."
                : "Enter a model ID before selecting this as Layal's API.");
    }

    private void ApplyLayalProviderProfile(ApiConnectionProfile profile)
    {
        ValidateLayalProviderProfile(profile, IsArabic);
        AppRuntime.Settings.ProviderBaseUrl = profile.BaseUrl.TrimEnd('/');
        AppRuntime.Settings.ModelId = profile.ModelId.Trim();
        AppRuntime.SecretStore.Save(profile.AuthSecret);
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        BaseUrlInput.Text = AppRuntime.Settings.ProviderBaseUrl;
        ModelInput.Text = AppRuntime.Settings.ModelId;
        ApiKeyInput.Clear();
        RefreshProviderStatus();
        SetAccessibleStatusText(ProviderActionStatus, IsArabic
            ? "تم تحديث إعداد API السحابي لليال. يظل الوضع المحلي هو الافتراضي؛ أوقفه من الإعدادات لاستخدام هذا المزود."
            : "Layal's cloud API settings were updated. Local AI remains the default; turn it off in Settings to use this provider.");
        ProviderActionStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
    }

    private void SaveApiConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = BuildApiConnectionProfile();
            var oldName = _editingApiConnectionName;
            var isNew = string.IsNullOrWhiteSpace(oldName);
            if (AppRuntime.ApiConnectionService.Find(profile.Name) is not null &&
                (isNew || !string.Equals(oldName, profile.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(IsArabic
                    ? "الاسم مستخدم بالفعل لاتصال آخر. استخدم اسمًا مختلفًا."
                    : "That name is already used by another connection. Choose a different name.");
            if (profile.IsLayalProvider) ValidateLayalProviderProfile(profile, IsArabic);
            AppRuntime.ApiConnectionService.Save(profile);
            if (!isNew && !string.Equals(oldName, profile.Name, StringComparison.OrdinalIgnoreCase))
                AppRuntime.ApiConnectionService.Delete(oldName!);
            if (profile.IsLayalProvider) ApplyLayalProviderProfile(profile);

            ApiConnectionSearchInput.Clear();
            if (isNew)
            {
                RefreshApiConnections();
                ApiConnectionList.SelectedItem = null;
                ClearApiConnectionForm();
                ApiConnectionsStatus.Text = IsArabic
                    ? $"تمت إضافة «{profile.Name}» إلى القائمة. أصبحت الخانات فارغة لإضافة API جديد."
                    : $"'{profile.Name}' was added to the list. The form is clear and ready for another API.";
            }
            else
            {
                RefreshApiConnections(profile.Name);
                ApiConnectionsStatus.Text = IsArabic
                    ? $"تم حفظ تعديلات «{profile.Name}»."
                    : $"Changes to '{profile.Name}' were saved.";
            }
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            ApiConnectionsStatus.Text = (IsArabic ? "تعذر حفظ اتصال API: " : "Could not save API connection: ") + ex.Message;
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private void UseAsLayalApiButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = BuildApiConnectionProfile();
            var oldName = _editingApiConnectionName;
            var isNew = string.IsNullOrWhiteSpace(oldName);
            if (AppRuntime.ApiConnectionService.Find(profile.Name) is not null &&
                (isNew || !string.Equals(oldName, profile.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(IsArabic
                    ? "الاسم مستخدم بالفعل لاتصال آخر. استخدم اسمًا مختلفًا."
                    : "That name is already used by another connection. Choose a different name.");
            profile.IsLayalProvider = true;
            ValidateLayalProviderProfile(profile, IsArabic);
            AppRuntime.ApiConnectionService.Save(profile);
            if (!isNew && !string.Equals(oldName, profile.Name, StringComparison.OrdinalIgnoreCase))
                AppRuntime.ApiConnectionService.Delete(oldName!);
            ApplyLayalProviderProfile(profile);
            ApiConnectionSearchInput.Clear();
            RefreshApiConnections(profile.Name);
            ApiConnectionsStatus.Text = IsArabic
                ? $"تم تعيين «{profile.Name}» كـ API السحابي لليال. يظل الشات المحلي هو الافتراضي."
                : $"'{profile.Name}' is now Layal's cloud API provider. Local chat remains the default.";
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            ApiConnectionsStatus.Text = (IsArabic ? "تعذر تعيين API ليال: " : "Could not set Layal API: ") + ex.Message;
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private void ChooseApiImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = IsArabic ? "اختيار صورة للتطبيق" : "Choose an application image",
            Filter = IsArabic
                ? "صور مدعومة|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|كل الملفات|*.*"
                : "Supported images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ApiConnectionImagePathInput.Text = CopyApiImageIntoStore(dialog.FileName);
            ApiConnectionsStatus.Text = IsArabic ? "تم حفظ نسخة محلية من الصورة." : "A local copy of the image was saved.";
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            ApiConnectionsStatus.Text = (IsArabic ? "تعذر استخدام الصورة: " : "Could not use this image: ") + ex.Message;
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private static string CopyApiImageIntoStore(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff"))
            throw new InvalidOperationException("Select a PNG, JPEG, BMP, GIF, or TIFF image.");
        var source = new FileInfo(sourcePath);
        if (!source.Exists || source.Length is 0 or > 5 * 1024 * 1024)
            throw new InvalidOperationException("API images must be non-empty and no larger than 5 MB.");
        var directory = Path.Combine(AppRuntime.DataRoot, "api-images");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
        File.Copy(source.FullName, destination, overwrite: false);
        return destination;
    }

    private void ClearApiImageButton_Click(object sender, RoutedEventArgs e) =>
        ApiConnectionImagePathInput.Clear();

    private async void TestApiConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        ApiConnectionProfile profile;
        try { profile = BuildApiConnectionProfile(); }
        catch (Exception ex)
        {
            ApiConnectionsStatus.Text = ex.Message;
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }

        TestApiConnectionButton.IsEnabled = false;
        ApiConnectionsStatus.Text = IsArabic ? "جارٍ إرسال طلب GET لمسار الاختبار..." : "Sending GET request to the configured test path...";
        ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.SteelBlue;
        try
        {
            var result = await AppRuntime.ApiConnectionService.TestAsync(profile, profile.TestPath);
            var bodyPreview = result.Body.Length > 700 ? result.Body[..700] + "…" : result.Body;
            ApiConnectionsStatus.Text = $"HTTP {result.StatusCode} {result.ReasonPhrase}{Environment.NewLine}{result.RequestUrl}{Environment.NewLine}{bodyPreview}";
            ApiConnectionsStatus.Foreground = result.StatusCode is >= 200 and < 300
                ? System.Windows.Media.Brushes.ForestGreen
                : System.Windows.Media.Brushes.DarkGoldenrod;
        }
        catch (Exception ex)
        {
            ApiConnectionsStatus.Text = (IsArabic ? "فشل اختبار API: " : "API test failed: ") + ex.Message;
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
        finally { TestApiConnectionButton.IsEnabled = true; }
    }

    private void DeleteApiConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ApiConnectionNameInput.Text.Trim();
        var selectedProfile = AppRuntime.ApiConnectionService.Find(name);
        if (string.IsNullOrWhiteSpace(name) || selectedProfile is null)
        {
            ApiConnectionsStatus.Text = IsArabic ? "حدد اتصالًا محفوظًا لحذفه." : "Select a saved connection to delete.";
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
            return;
        }

        var answer = LayalDialogService.Show(this,
            IsArabic ? $"هل تريد حذف اتصال API «{name}» وبيانات المصادقة المحفوظة؟" : $"Delete API connection '{name}' and its stored authentication data?",
            IsArabic ? "تأكيد حذف الاتصال" : "Confirm API connection deletion",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        AppRuntime.ApiConnectionService.Delete(name);
        if (selectedProfile.IsLayalProvider)
        {
            AppRuntime.Settings.ProviderBaseUrl = string.Empty;
            AppRuntime.Settings.ModelId = string.Empty;
            AppRuntime.SecretStore.Delete();
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            BaseUrlInput.Text = string.Empty;
            ModelInput.Text = string.Empty;
            ApiKeyInput.Clear();
                RefreshProviderStatus();
        }
        ApiConnectionSearchInput.Clear();
        RefreshApiConnections();
        ApiConnectionsStatus.Text = IsArabic ? "تم حذف الاتصال." : "Connection deleted.";
        ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
    }


    private void ClockTimer_Tick(object? sender, EventArgs e) => UpdateClockDisplay();

    private async void WeatherRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (AppRuntime.Settings.UseAutomaticLocation &&
            AppRuntime.Settings.AutomaticLocationConsentConfirmed)
            await RefreshLocationWeatherAsync();
        else if (!AppRuntime.Settings.UseAutomaticLocation)
            await RefreshWeatherAsync();
    }

    private static DateTimeOffset ConvertToWeatherTime(DateTimeOffset instant, CurrentWeatherReport report)
    {
        try { return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(report.TimeZoneId)); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException || exception is InvalidTimeZoneException)
        {
            var offsetSeconds = Math.Clamp(report.UtcOffsetSeconds, -14 * 60 * 60, 14 * 60 * 60);
            return instant.ToOffset(TimeSpan.FromSeconds(offsetSeconds));
        }
    }

    private void UpdateClockDisplay()
    {
        var now = _currentWeatherReport is { } report ? ConvertToWeatherTime(DateTimeOffset.UtcNow, report) : DateTimeOffset.Now;
        var culture = CultureInfo.GetCultureInfo(IsArabic ? "ar-EG" : "en-US");
        ClockTimeText.Text = now.ToString("HH:mm:ss", culture);
        CurrentDateText.Text = now.ToString(IsArabic ? "dddd، dd MMMM yyyy" : "dddd, MMMM dd yyyy", culture);
    }

    private void UpdateWeatherDisplay()
    {
        var ar = IsArabic;
        var requestedLocation = AppRuntime.Settings.WeatherLocation?.Trim() ?? string.Empty;
        var automaticLocationEnabled = AppRuntime.Settings.UseAutomaticLocation &&
            AppRuntime.Settings.AutomaticLocationConsentConfirmed;
        if (_currentWeatherReport is null)
        {
            WeatherIconText.Text = automaticLocationEnabled ? "📍" :
                (string.IsNullOrWhiteSpace(requestedLocation) ? "🌍" : "🌡");
            WeatherTemperatureText.Text = "—°C";
            WeatherLocationText.Text = automaticLocationEnabled
                ? (ar ? "بانتظار تحديد الموقع" : "Waiting for location")
                : (string.IsNullOrWhiteSpace(requestedLocation)
                    ? (ar ? "لم تُحدّد مدينة بديلة" : "No fallback city selected") : requestedLocation);
            WeatherSummaryText.Text = automaticLocationEnabled
                ? (ar ? "سيتم تحديث الطقس بعد السماح الصريح باستخدام موقع الجهاز." : "Weather will update after you explicitly opt in to device location.")
                : (string.IsNullOrWhiteSpace(requestedLocation)
                    ? (ar ? "اضغط «اكتشاف موقعي» للموافقة على مشاركة الإحداثيات التقريبية، أو أدخل مدينة يدويًا." : "Select Detect my location to consent to sharing approximate coordinates, or enter a city manually.")
                    : (ar ? "لم يتم تحميل الطقس بعد." : "Weather has not loaded yet."));
            WeatherDetailsText.Text = string.Empty;
            WeatherUpdatedText.Text = string.Empty;
            return;
        }

        var report = _currentWeatherReport;
        WeatherIconText.Text = WeatherService.GetConditionIcon(report.WeatherCode);
        WeatherTemperatureText.Text = $"{report.TemperatureC:0.#}°C";
        WeatherLocationText.Text = report.LocationDisplay;
        WeatherSummaryText.Text = WeatherService.GetConditionText(report.WeatherCode, ar);
        WeatherDetailsText.Text = ar
            ? $"المحسوسة {report.ApparentTemperatureC:0.#}°C · الرطوبة {report.RelativeHumidityPercent}% · الرياح {report.WindSpeedKmh:0.#} كم/س"
            : $"Feels like {report.ApparentTemperatureC:0.#}°C · Humidity {report.RelativeHumidityPercent}% · Wind {report.WindSpeedKmh:0.#} km/h";
        var culture = CultureInfo.GetCultureInfo(ar ? "ar-EG" : "en-US");
        var updatedAt = ConvertToWeatherTime(report.FetchedAt, report);
        WeatherUpdatedText.Text = ar
            ? $"آخر تحديث {updatedAt.ToString("HH:mm", culture)} · البيانات: Open-Meteo"
            : $"Updated {updatedAt.ToString("HH:mm", culture)} · Weather data: Open-Meteo";
    }

    private void RefreshWeatherButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppRuntime.Settings.UseAutomaticLocation &&
            AppRuntime.Settings.AutomaticLocationConsentConfirmed)
        {
            _ = RefreshLocationWeatherAsync();
            return;
        }
        if (string.IsNullOrWhiteSpace(AppRuntime.Settings.WeatherLocation))
        {
            MainTabs.SelectedItem = SettingsTab;
            WeatherLocationInput.Focus();
            WeatherSettingsStatus.Text = IsArabic
                ? "اختر «اكتشاف موقعي» ووافق على مشاركة الإحداثيات التقريبية، أو أدخل مدينة يدويًا."
                : "Select Detect my location and consent to approximate-coordinate sharing, or enter a city manually.";
            return;
        }
        _ = RefreshWeatherAsync();
    }

    private async Task RefreshLocationWeatherAsync()
    {
        // Defense in depth: no caller (startup, timer, or header refresh) may request
        // device coordinates unless the user explicitly confirmed the disclosure.
        if (!AppRuntime.Settings.UseAutomaticLocation ||
            !AppRuntime.Settings.AutomaticLocationConsentConfirmed ||
            _weatherRequestInProgress || !IsLoaded) return;
        _weatherRequestInProgress = true;
        RefreshWeatherButton.IsEnabled = false;
        UseMyLocationButton.IsEnabled = false;
        SaveWeatherLocationButton.IsEnabled = false;
        var cancellation = new CancellationTokenSource();
        _weatherCancellation = cancellation;
        WeatherSummaryText.Text = IsArabic ? "جارٍ تحديد موقعك..." : "Detecting your location...";
        WeatherUpdatedText.Text = string.Empty;
        try
        {
            var coordinates = await _deviceLocationService.GetCurrentLocationAsync();
            if (cancellation.IsCancellationRequested) return;
            _currentWeatherReport = await _weatherService.GetCurrentWeatherAtCoordinatesAsync(
                coordinates.Latitude, coordinates.Longitude, IsArabic, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            AppRuntime.Settings.UseAutomaticLocation = true;
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            UpdateClockDisplay();
            UpdateWeatherDisplay();
            WeatherSettingsStatus.Text = IsArabic
                ? "تم تحديد موقعك وتحديث الوقت والتاريخ والطقس."
                : "Location detected; time, date, and weather are updated.";
            WeatherSettingsStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (DeviceLocationAccessException)
        {
            if (cancellation.IsCancellationRequested) return;
            AppRuntime.Settings.UseAutomaticLocation = false;
            AppRuntime.Settings.AutomaticLocationConsentConfirmed = false;
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            _currentWeatherReport = null;
            UpdateClockDisplay();
            UpdateWeatherDisplay();
            WeatherIconText.Text = "📍";
            WeatherSummaryText.Text = IsArabic
                ? "لم يُسمح بالوصول للموقع. فعّل خدمات الموقع في إعدادات Windows ثم اضغط «اكتشاف موقعي»، أو استخدم مدينة يدويًا."
                : "Location access was not granted. Enable Windows Location and select Detect my location, or use a city manually.";
            WeatherSettingsStatus.Text = WeatherSummaryText.Text;
            WeatherSettingsStatus.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (cancellation.IsCancellationRequested) return;
            if (_currentWeatherReport is not null) UpdateWeatherDisplay();
            else
            {
                WeatherIconText.Text = "⚠";
                WeatherTemperatureText.Text = "—°C";
                WeatherDetailsText.Text = string.Empty;
                WeatherSummaryText.Text = IsArabic
                    ? "تعذّر تحديد الموقع أو الاتصال بخدمة الطقس. تحقّق من خدمات الموقع والإنترنت، أو أدخل مدينة يدويًا."
                    : "Could not detect location or reach the weather service. Check Windows Location and internet, or enter a city manually.";
            }
            WeatherUpdatedText.Text = IsArabic ? "تعذّر التحديث؛ قد تكون البيانات المعروضة قديمة." : "Refresh failed; displayed data may be stale.";
            WeatherSettingsStatus.Text = WeatherSummaryText.Text;
            WeatherSettingsStatus.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
        }
        finally
        {
            if (ReferenceEquals(_weatherCancellation, cancellation)) _weatherCancellation = null;
            _weatherRequestInProgress = false;
            RefreshWeatherButton.IsEnabled = true;
            UseMyLocationButton.IsEnabled = true;
            SaveWeatherLocationButton.IsEnabled = true;
            cancellation.Dispose();
        }
    }

    private async Task RefreshWeatherAsync()
    {
        if (_weatherRequestInProgress || !IsLoaded) return;
        var location = AppRuntime.Settings.WeatherLocation?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(location)) { UpdateWeatherDisplay(); return; }
        _weatherRequestInProgress = true;
        RefreshWeatherButton.IsEnabled = false;
        UseMyLocationButton.IsEnabled = false;
        SaveWeatherLocationButton.IsEnabled = false;
        var cancellation = new CancellationTokenSource();
        _weatherCancellation = cancellation;
        WeatherSummaryText.Text = IsArabic ? "جارٍ تحديث الطقس..." : "Refreshing weather...";
        WeatherUpdatedText.Text = string.Empty;
        try
        {
            _currentWeatherReport = await _weatherService.GetCurrentWeatherAsync(location, IsArabic, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            UpdateClockDisplay();
            UpdateWeatherDisplay();
            WeatherSettingsStatus.Text = IsArabic ? "تم تحديث حالة الطقس للمدينة اليدوية." : "Weather updated for the manual city.";
            WeatherSettingsStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (WeatherLocationNotFoundException)
        {
            if (cancellation.IsCancellationRequested) return;
            _currentWeatherReport = null;
            UpdateWeatherDisplay();
            WeatherIconText.Text = "❔";
            WeatherSummaryText.Text = IsArabic
                ? "لم نعثر على المدينة. جرّب كتابة اسم المدينة والدولة."
                : "City not found. Try entering the city and country.";
            WeatherUpdatedText.Text = string.Empty;
            WeatherSettingsStatus.Text = WeatherSummaryText.Text;
            WeatherSettingsStatus.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (cancellation.IsCancellationRequested) return;
            if (_currentWeatherReport is not null) UpdateWeatherDisplay();
            if (_currentWeatherReport is null)
            {
                WeatherIconText.Text = "⚠";
                WeatherTemperatureText.Text = "—°C";
                WeatherDetailsText.Text = string.Empty;
                WeatherSummaryText.Text = IsArabic
                    ? "تعذّر الاتصال بخدمة الطقس. تحقّق من الإنترنت وحاول مجددًا."
                    : "Weather service unavailable. Check the internet connection and retry.";
            }
            WeatherUpdatedText.Text = IsArabic ? "تعذّر التحديث؛ قد تكون البيانات المعروضة قديمة." : "Refresh failed; displayed data may be stale.";
            WeatherSettingsStatus.Text = WeatherSummaryText.Text;
            WeatherSettingsStatus.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
        }
        finally
        {
            if (ReferenceEquals(_weatherCancellation, cancellation)) _weatherCancellation = null;
            _weatherRequestInProgress = false;
            RefreshWeatherButton.IsEnabled = true;
            UseMyLocationButton.IsEnabled = true;
            SaveWeatherLocationButton.IsEnabled = true;
            cancellation.Dispose();
        }
    }

    private async void UseMyLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AppRuntime.Settings.AutomaticLocationConsentConfirmed)
        {
            var consent = LayalDialogService.Show(this,
                IsArabic
                    ? "لإظهار الطقس والوقت المحلي، سيطلب Windows إذن الوصول إلى موقع تقريبي (دقة مستهدفة نحو كيلومتر واحد). عند الموافقة، ستُرسل الإحداثيات إلى Open-Meteo لجلب الطقس والمنطقة الزمنية. لا تحفظ ليال الإحداثيات في الإعدادات ولا ترسلها إلى مزود AI. يمكنك الرفض أو استخدام مدينة يدويًا بدلًا من ذلك. هل تريد المتابعة إلى طلب إذن Windows؟"
                    : "To show local weather and time, Windows will ask to access an approximate device location (target accuracy about 1 km). If allowed, coordinates are sent to Open-Meteo for weather and time-zone data. Layal does not save the coordinates in settings or send them to an AI provider. You can decline or use a city manually instead. Continue to the Windows permission request?",
                IsArabic ? "موافقة مشاركة الموقع للطقس" : "Consent to use location for weather",
                MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
            if (consent != MessageBoxResult.Yes)
                return;
            AppRuntime.Settings.AutomaticLocationConsentConfirmed = true;
        }

        AppRuntime.Settings.UseAutomaticLocation = true;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        _currentWeatherReport = null;
        UpdateClockDisplay();
        UpdateWeatherDisplay();
        WeatherSettingsStatus.Text = IsArabic
            ? "سيتم استخدام موقع الجهاز بعد السماح من Windows..."
            : "Windows permission is needed to use the device location...";
        WeatherSettingsStatus.Foreground = ThemeBrush("Layal.Muted", System.Windows.Media.Brushes.Gray);
        await RefreshLocationWeatherAsync();
    }

    private async void SaveWeatherLocationButton_Click(object sender, RoutedEventArgs e)
    {
        var location = WeatherLocationInput.Text.Trim();
        if (location.Length > 120)
        {
            WeatherSettingsStatus.Text = IsArabic
                ? "اسم المدينة طويل جدًا؛ استخدم 120 حرفًا أو أقل."
                : "City name is too long; use 120 characters or fewer.";
            WeatherSettingsStatus.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
            return;
        }
        AppRuntime.Settings.WeatherLocation = location;
        AppRuntime.Settings.UseAutomaticLocation = false;
        // Choosing a manual city also revokes Layal's saved opt-in for future device-location reads.
        AppRuntime.Settings.AutomaticLocationConsentConfirmed = false;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        _currentWeatherReport = null;
        UpdateClockDisplay();
        UpdateWeatherDisplay();
        if (string.IsNullOrWhiteSpace(location))
        {
            WeatherSettingsStatus.Text = IsArabic
                ? "تم مسح المدينة اليدوية؛ اضغط «اكتشاف موقعي» لتفعيل الموقع التلقائي."
                : "Fallback city cleared; select Detect my location to enable automatic location.";
            WeatherSettingsStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
            return;
        }
        WeatherSettingsStatus.Text = IsArabic ? "تم حفظ المدينة. جارٍ جلب الطقس..." : "City saved. Fetching weather...";
        WeatherSettingsStatus.Foreground = ThemeBrush("Layal.Muted", System.Windows.Media.Brushes.Gray);
        await RefreshWeatherAsync();
    }

}
