using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Layal.Models;

namespace Layal.Services;

public sealed record LocalAiProgress(string Message, double? Percent = null);

/// <summary>
/// Runs local chat through Ollama's loopback-only API. The local mode never uses an
/// API key and never makes model requests to a remote AI endpoint.
/// </summary>
public sealed class LocalAiService : IAgentModelClient
{
    public const string DefaultModelId = "qwen3.5:2b";
    public static readonly Uri ApiBaseUri = new("http://127.0.0.1:11434/");
    public static string DataRoot
    {
        get
        {
            var paths = LayalDataPathPolicy.Resolve(
                Environment.GetEnvironmentVariable(LayalDataPathPolicy.AcceptanceRootEnvironmentVariable),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            return Path.Combine(paths.DataRoot, "LocalAi");
        }
    }

    private static bool IsIsolatedAcceptanceMode =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LayalDataPathPolicy.AcceptanceRootEnvironmentVariable));

    private const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";
    private const long MaximumInstallerBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromMinutes(10);
    private static readonly HttpClient ApiClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        UseProxy = false,
        Proxy = null,
        AllowAutoRedirect = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly HttpClient DownloadClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private const int MaximumAgentResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _agentApiClient;

    public LocalAiService() : this(ApiClient) { }

    /// <summary>Injectable HTTP transport for deterministic local-agent protocol tests.</summary>
    public LocalAiService(HttpClient agentApiClient) =>
        _agentApiClient = agentApiClient ?? throw new ArgumentNullException(nameof(agentApiClient));

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
        // Live acceptance must not inspect or communicate with a user-installed Ollama service.
        if (IsIsolatedAcceptanceMode) return false;

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
        if (IsIsolatedAcceptanceMode)
            throw new InvalidOperationException("Local AI installation and model setup are disabled during isolated live acceptance.");

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
        if (IsIsolatedAcceptanceMode)
            throw new InvalidOperationException("Local AI chat is disabled during isolated live acceptance.");

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

    public async Task<AgentTurnResult> AgentTurnAsync(
        AgentModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Provider != AgentModelProvider.Local)
            throw new InvalidOperationException("The local Ollama client only accepts local-provider requests.");
        if (!string.IsNullOrWhiteSpace(request.BaseUrl) || !string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException("Local agent requests must not contain a remote provider URL or API key.");
        if (IsIsolatedAcceptanceMode)
            throw new InvalidOperationException("Local AI agent tasks are disabled during isolated live acceptance.");
        ArgumentNullException.ThrowIfNull(request.Messages);
        ArgumentNullException.ThrowIfNull(request.ToolDefinitions);
        if (request.Messages.Count == 0)
            throw new ArgumentException("At least one agent message is required.", nameof(request));
        if (request.ToolDefinitions.Count == 0)
            throw new ArgumentException("At least one tool definition is required.", nameof(request));

        var model = ValidateModelId(request.Model);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = NormalizeOllamaMessages(request.Messages),
            ["tools"] = JsonNode.Parse(request.ToolDefinitions.ToJsonString())!.AsArray(),
            ["stream"] = false,
            ["options"] = new JsonObject
            {
                ["num_ctx"] = 8192,
                ["num_predict"] = 2048
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBaseUri, "api/chat"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await _agentApiClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadBoundedAgentErrorAsync(response.Content, timeout.Token);
            throw new InvalidOperationException(
                $"Local Ollama agent request failed (HTTP {(int)response.StatusCode}). {detail}");
        }

        var root = await ReadBoundedAgentJsonAsync(response.Content, timeout.Token);
        var message = root["message"] as JsonObject
            ?? throw new InvalidOperationException("Ollama returned no assistant message for the local agent.");

        var content = GetJsonString(message["content"]);
        var calls = new List<AgentToolCall>();
        var normalizedCalls = new JsonArray();
        if (message["tool_calls"] is JsonArray returnedCalls)
        {
            foreach (var item in returnedCalls)
            {
                if (item is not JsonObject call || call["function"] is not JsonObject function)
                    throw new InvalidOperationException("Ollama returned a malformed tool call; the task was stopped safely.");

                var name = GetJsonString(function["name"]);
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("Ollama returned a tool call without a function name; the task was stopped safely.");

                var arguments = ParseToolArguments(function["arguments"]);
                var argumentsJson = arguments.ToJsonString();
                var id = "ollama_" + Guid.NewGuid().ToString("N");
                calls.Add(new AgentToolCall(id, name, argumentsJson));
                normalizedCalls.Add(new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = name,
                        ["arguments"] = argumentsJson
                    }
                });
            }
        }

        var assistantMessage = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content
        };
        if (normalizedCalls.Count > 0)
            assistantMessage["tool_calls"] = normalizedCalls;

        return new AgentTurnResult(assistantMessage, content, calls);
    }

    public async Task ValidateToolCallingAsync(
        AgentModelProvider provider,
        string model,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (provider != AgentModelProvider.Local)
            throw new InvalidOperationException("Ollama can only validate the local provider.");

        var probeTools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "layal_tool_call_probe",
                    ["description"] = "Capability check only. Do not perform any external action.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                        ["additionalProperties"] = false
                    }
                }
            }
        };
        var probeMessages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = "You are checking structured tool-calling capability. Call the only available function exactly once. Do not answer in prose."
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Call the capability-check function now with an empty argument object."
            }
        };

        var result = await AgentTurnAsync(
            new AgentModelRequest(AgentModelProvider.Local, model, probeMessages, probeTools),
            cancellationToken);
        if (!result.ToolCalls.Any(call =>
                string.Equals(call.Name, "layal_tool_call_probe", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The local model '{ValidateModelId(model)}' did not return a structured tool call during the capability check. " +
                "This model/runtime combination cannot safely run Layal agent tasks yet. Choose an Ollama model that passes tool calling and retry. " +
                "No cloud provider or API key was used as a fallback.");
        }
    }

    private static JsonArray NormalizeOllamaMessages(JsonArray messages)
    {
        var toolNamesById = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalized = new JsonArray();

        foreach (var source in messages)
        {
            if (source is not JsonObject message)
                throw new InvalidOperationException("The local agent conversation contains an invalid message.");

            var role = GetJsonString(message["role"]);
            if (role is not ("system" or "user" or "assistant" or "tool"))
                throw new InvalidOperationException("The local agent conversation contains an unsupported message role.");

            var target = new JsonObject
            {
                ["role"] = role,
                ["content"] = GetJsonString(message["content"])
            };

            if (role == "assistant" && message["tool_calls"] is JsonArray sourceCalls)
            {
                var targetCalls = new JsonArray();
                foreach (var sourceCall in sourceCalls)
                {
                    if (sourceCall is not JsonObject call || call["function"] is not JsonObject function)
                        throw new InvalidOperationException("The local agent conversation contains a malformed assistant tool call.");

                    var id = GetJsonString(call["id"]);
                    var name = GetJsonString(function["name"]);
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                        throw new InvalidOperationException("A local assistant tool call is missing its ID or function name.");

                    var arguments = ParseToolArguments(function["arguments"]);
                    toolNamesById[id] = name;
                    targetCalls.Add(new JsonObject
                    {
                        ["function"] = new JsonObject
                        {
                            ["name"] = name,
                            ["arguments"] = arguments
                        }
                    });
                }

                target["tool_calls"] = targetCalls;
            }
            else if (role == "tool")
            {
                var callId = GetJsonString(message["tool_call_id"]);
                if (string.IsNullOrWhiteSpace(callId) || !toolNamesById.TryGetValue(callId, out var toolName))
                    throw new InvalidOperationException("A local tool result is not linked to a preceding assistant tool call.");

                target["tool_name"] = toolName;
            }

            normalized.Add(target);
        }

        return normalized;
    }

    private static JsonObject ParseToolArguments(JsonNode? arguments)
    {
        if (arguments is JsonObject objectArguments)
            return (JsonObject)objectArguments.DeepClone();

        if (arguments is JsonValue value && value.TryGetValue<string>(out var raw))
        {
            try
            {
                return JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw) as JsonObject
                    ?? throw new InvalidOperationException("A local tool call's arguments must be a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("A local tool call returned invalid JSON arguments.", ex);
            }
        }

        if (arguments is null)
            return new JsonObject();

        throw new InvalidOperationException("A local tool call's arguments must be a JSON object.");
    }

    private static string GetJsonString(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var result)
            ? result ?? string.Empty
            : string.Empty;

    private static async Task<JsonObject> ReadBoundedAgentJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength.HasValue &&
            content.Headers.ContentLength.Value > MaximumAgentResponseBytes)
            throw new InvalidOperationException("The local model response exceeded Layal's 4 MB safety limit.");

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var remaining = MaximumAgentResponseBytes + 1 - (int)output.Length;
                var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaximumAgentResponseBytes)
                    throw new InvalidOperationException("The local model response exceeded Layal's 4 MB safety limit.");
                output.Write(buffer, 0, read);
            }

            var bytes = output.ToArray();
            try
            {
                return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
                    ?? throw new InvalidOperationException("Ollama returned an invalid local-agent response.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Ollama returned invalid JSON for the local agent.", ex);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (output.TryGetBuffer(out var segment) && segment.Array is not null)
                Array.Clear(segment.Array, segment.Offset, segment.Count);
        }
    }

    private static async Task<string> ReadBoundedAgentErrorAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            while (output.Length < 32 * 1024)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, 32 * 1024 - output.Length)),
                    cancellationToken);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
            var detail = Encoding.UTF8.GetString(output.ToArray());
            return Truncate(detail, 800);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            return "No bounded error details were available from the local Ollama service.";
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (output.TryGetBuffer(out var segment) && segment.Array is not null)
                Array.Clear(segment.Array, segment.Offset, segment.Count);
        }
    }

    private async Task PullModelAsync(string modelId, IProgress<LocalAiProgress>? progress, CancellationToken cancellationToken)
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
