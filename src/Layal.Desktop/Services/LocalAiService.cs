using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Layal.Models;

namespace Layal.Services;

public sealed record LocalAiProgress(string Message, double? Percent = null);

/// <summary>
/// Runs local chat through Ollama's loopback-only API. The local mode never uses an
/// API key and never makes model requests to a remote AI endpoint.
/// </summary>
public sealed class LocalAiService
{
    public const string DefaultModelId = "qwen3.5:2b";
    public static readonly Uri ApiBaseUri = new("http://127.0.0.1:11434/");
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Layal", "LocalAi");

    private const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";
    private const long MaximumInstallerBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromMinutes(10);
    private static readonly HttpClient ApiClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        UseProxy = false,
        Proxy = null
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly HttpClient DownloadClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static string ValidateModelId(string? modelId)
    {
        var value = (modelId ?? string.Empty).Trim();
        if (value.Length is < 1 or > 100 ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '/')) ||
            value.StartsWith('/') || value.Contains("..", StringComparison.Ordinal) ||
            value.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The local model identifier is invalid.");
        }
        return value;
    }

    public async Task<bool> IsReadyAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        var target = ValidateModelId(modelId ?? DefaultModelId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await ApiClient.GetAsync(new Uri(ApiBaseUri, "api/tags"), timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return false;
            return models.EnumerateArray().Any(item =>
            {
                var name = GetString(item, "name");
                if (name.Length == 0) name = GetString(item, "model");
                return string.Equals(name, target, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            return false;
        }
    }

    public async Task EnsureReadyAsync(
        string? modelId = null,
        IProgress<LocalAiProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var target = ValidateModelId(modelId ?? DefaultModelId);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            throw new PlatformNotSupportedException("Local AI setup currently requires 64-bit Windows.");

        Directory.CreateDirectory(DataRoot);
        if (!await IsServerAvailableAsync(cancellationToken))
        {
            var installed = FindOllamaExecutable();
            if (installed is not null)
            {
                progress?.Report(new LocalAiProgress("تشغيل محرك الذكاء الاصطناعي المحلي…"));
                StartOllama(installed);
            }
            else
            {
                progress?.Report(new LocalAiProgress("تنزيل مُثبّت Ollama الرسمي…"));
                var installerPath = Path.Combine(DataRoot, "OllamaSetup.exe");
                await DownloadInstallerAsync(installerPath, progress, cancellationToken);
                await VerifyOllamaSignatureAsync(installerPath, cancellationToken);
                progress?.Report(new LocalAiProgress("فتح مُثبّت Ollama؛ أكمل نافذة التثبيت التي ستظهر. لن يحتاج التثبيت إلى صلاحيات المسؤول وفق تعليمات Ollama."));
                Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
            }

            await WaitForServerAsync(progress, cancellationToken);
        }

        if (await IsReadyAsync(target, cancellationToken))
        {
            progress?.Report(new LocalAiProgress("نموذج الشات المحلي جاهز."));
            return;
        }

        await PullModelAsync(target, progress, cancellationToken);
        if (!await IsReadyAsync(target, cancellationToken))
            throw new InvalidOperationException("اكتمل تنزيل النموذج، لكن Ollama لم يعرضه في قائمة النماذج المحلية. أعد المحاولة من الإعدادات.");
        progress?.Report(new LocalAiProgress("الشات المحلي جاهز. لم يُستخدم أي مفتاح API."));
    }

    public async Task<string> ChatStreamAsync(
        string? modelId,
        IReadOnlyList<ChatMessage> messages,
        Action<string> onDelta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(onDelta);
        var target = ValidateModelId(modelId ?? DefaultModelId);
        if (messages.Count == 0) throw new ArgumentException("At least one chat message is required.", nameof(messages));

        var payload = new
        {
            model = target,
            stream = true,
            messages = messages.Select(message => new
            {
                role = message.Role,
                content = message.Content
            }).ToArray(),
            options = new { num_ctx = 4096, num_predict = 1024 }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBaseUri, "api/chat"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var response = await ApiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadLimitedErrorAsync(response, cancellationToken);
            throw new InvalidOperationException(
                $"تعذر استخدام نموذج الشات المحلي (HTTP {(int)response.StatusCode}). {detail} تأكد أن Ollama يعمل محليًا ثم أعد المحاولة.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body, Encoding.UTF8);
        var answer = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var error = GetString(root, "error");
            if (error.Length > 0)
                throw new InvalidOperationException("تعذر إنشاء الرد من النموذج المحلي: " + Truncate(error, 800));

            if (root.TryGetProperty("message", out var message))
            {
                var delta = GetString(message, "content");
                if (delta.Length > 0)
                {
                    answer.Append(delta);
                    onDelta(delta);
                }
            }

            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                break;
        }

        var result = answer.ToString().Trim();
        if (result.Length == 0)
            throw new InvalidOperationException("أعاد نموذج الشات المحلي ردًا فارغًا. جرّب إرسال الرسالة مرة أخرى.");
        return result;
    }

    private static async Task PullModelAsync(string modelId, IProgress<LocalAiProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new LocalAiProgress($"تنزيل نموذج الشات {modelId} إلى جهازك…"));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBaseUri, "api/pull"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { model = modelId, stream = true }),
                Encoding.UTF8,
                "application/json")
        };
        using var response = await ApiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"فشل تنزيل النموذج المحلي (HTTP {(int)response.StatusCode}). تأكد من اتصال الإنترنت والمساحة الفارغة ثم أعد المحاولة.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lastReport = DateTimeOffset.MinValue;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var error = GetString(root, "error");
            if (error.Length > 0)
                throw new InvalidOperationException("فشل تنزيل نموذج الشات المحلي: " + Truncate(error, 800));

            var status = GetString(root, "status");
            double? percent = null;
            if (TryGetNonNegativeInt64(root, "total", out var total) &&
                TryGetNonNegativeInt64(root, "completed", out var completed) && total > 0)
            {
                percent = Math.Clamp(completed * 100d / total, 0d, 100d);
            }

            if (percent is null || DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMilliseconds(400))
            {
                progress?.Report(new LocalAiProgress(
                    string.IsNullOrWhiteSpace(status) ? "تنزيل نموذج الشات المحلي…" : "تنزيل النموذج المحلي: " + status,
                    percent));
                lastReport = DateTimeOffset.UtcNow;
            }

            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ||
                (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True))
            {
                return;
            }
        }

        if (!await IsReadyAsync(modelId, cancellationToken))
            throw new InvalidOperationException("انقطع اتصال تنزيل نموذج الشات قبل اكتماله. أعد المحاولة؛ لن تُرسل المحادثات إلى خادم خارجي.");
    }

    private static async Task<bool> IsServerAvailableAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await ApiClient.GetAsync(new Uri(ApiBaseUri, "api/tags"), timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            return false;
        }
    }

    private static string? FindOllamaExecutable()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama");
        foreach (var name in new[] { "ollama app.exe", "ollama.exe" })
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static void StartOllama(string executable)
    {
        var isApp = Path.GetFileName(executable).Equals("ollama app.exe", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = isApp,
            CreateNoWindow = !isApp,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        if (!isApp) start.ArgumentList.Add("serve");
        Process.Start(start);
    }

    private static async Task WaitForServerAsync(IProgress<LocalAiProgress>? progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var nextReport = DateTimeOffset.MinValue;
        while (DateTimeOffset.UtcNow - started < ServerStartTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsServerAvailableAsync(cancellationToken)) return;
            if (DateTimeOffset.UtcNow >= nextReport)
            {
                progress?.Report(new LocalAiProgress(
                    "في انتظار تشغيل Ollama. إذا ظهرت نافذة التثبيت، أكمل خطواتها؛ سيبقى الصوت والمحادثة على جهازك."));
                nextReport = DateTimeOffset.UtcNow.AddSeconds(8);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException(
            "لم يبدأ خادم Ollama المحلي خلال عشر دقائق. أكمل نافذة التثبيت إن كانت مفتوحة، أو افتح Ollama من قائمة Start، ثم اضغط تجهيز الشات والصوت مرة أخرى.");
    }

    private static async Task DownloadInstallerAsync(
        string destination,
        IProgress<LocalAiProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".download";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await DownloadClient.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumInstallerBytes)
                throw new InvalidOperationException("حجم مُثبّت Ollama أكبر من الحد الآمن المتوقع.");

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
            var buffer = new byte[1024 * 128];
            long total = 0;
            try
            {
                while (true)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(), timeout.Token);
                    if (count == 0) break;
                    total += count;
                    if (total > MaximumInstallerBytes)
                        throw new InvalidOperationException("تجاوز مُثبّت Ollama حد التنزيل الآمن (2 جيجابايت).");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    var expectedLength = response.Content.Headers.ContentLength;
                    if (expectedLength.HasValue && expectedLength.Value > 0)
                    {
                        progress?.Report(new LocalAiProgress("تنزيل مُثبّت Ollama الرسمي…",
                            Math.Clamp(total * 100d / expectedLength.Value, 0d, 100d)));
                    }
                }
            }
            finally { Array.Clear(buffer); }
            await output.FlushAsync(timeout.Token);
            if (total < 1_000_000)
                throw new InvalidOperationException("ملف مُثبّت Ollama الذي تم تنزيله أصغر من المتوقع؛ أُلغي التثبيت.");

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task VerifyOllamaSignatureAsync(string installerPath, CancellationToken cancellationToken)
    {
        var escapedPath = installerPath.Replace("'", "''", StringComparison.Ordinal);
        var script = "$s = Get-AuthenticodeSignature -LiteralPath '" + escapedPath +
                     "'; if ($s.Status -eq 'Valid' -and $s.SignerCertificate -and $s.SignerCertificate.Subject -match 'Ollama') { exit 0 } else { exit 2 }";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(script);
        if (!process.Start())
            throw new InvalidOperationException("تعذر التحقق من التوقيع الرقمي لمُثبّت Ollama.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "لم نتمكن من إثبات التوقيع الرقمي لمُثبّت Ollama. لم نشغّل الملف لحماية جهازك؛ أعد المحاولة من الإعدادات أو ثبّت Ollama يدويًا من https://ollama.com/download.");
    }

    private static async Task<string> ReadLimitedErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return Truncate(text, 800);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return "لم تُقرأ تفاصيل الخطأ من الخادم المحلي.";
        }
    }

    private static bool TryGetNonNegativeInt64(JsonElement element, string property, out long value)
    {
        value = 0;
        return element.TryGetProperty(property, out var node) &&
               node.ValueKind == JsonValueKind.Number &&
               node.TryGetInt64(out value) && value >= 0;
    }

    private static string GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";
}
