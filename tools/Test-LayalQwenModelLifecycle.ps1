param(
    [string]$OllamaExePath = $env:OLLAMA_EXE_PATH,
    [string]$EvidenceDirectory = (Join-Path $env:GITHUB_WORKSPACE "artifacts/qwen-model-lifecycle")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($OllamaExePath)) {
    $candidates = @(
        (Get-Command ollama.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
        (Join-Path $env:LOCALAPPDATA "Programs/Ollama/ollama.exe"),
        (Join-Path $env:ProgramFiles "Ollama/ollama.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $OllamaExePath = $candidates | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($OllamaExePath) -or -not (Test-Path -LiteralPath $OllamaExePath -PathType Leaf)) {
    throw "Ollama executable was not found. Install the official Ollama Windows package first."
}

$root = Join-Path $env:RUNNER_TEMP ("layal-qwen-lifecycle-" + [guid]::NewGuid().ToString("N"))
$modelDirectory = Join-Path $root "models"
$logDirectory = Join-Path $root "logs"
New-Item -ItemType Directory -Path $modelDirectory, $logDirectory, $EvidenceDirectory -Force | Out-Null

# This disposable store is the only directory used for model blobs/manifests.
$env:OLLAMA_MODELS = $modelDirectory
$env:OLLAMA_HOST = "127.0.0.1:11434"
$env:OLLAMA_NUM_PARALLEL = "1"
$env:OLLAMA_MAX_LOADED_MODELS = "1"

$stdoutPath = Join-Path $logDirectory "ollama-serve.stdout.log"
$stderrPath = Join-Path $logDirectory "ollama-serve.stderr.log"
$server = $null
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan
$summary = [ordered]@{
    Result = "failed"
    Model = "qwen3.5:2b"
    IsolatedModelsDirectory = $modelDirectory
    RegistryAuthTest = "not-run"
    CancelledAfterBytes = 0L
    ResumedFromBytes = 0L
    FinalDigest = $null
    DownloadedBlobBytes = 0L
    ChatHealth = "not-run"
    Rollback = "not-run"
    CompletedAtUtc = $null
}

function Invoke-OllamaCli {
    param([string[]]$Arguments)
    & $OllamaExePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ollama $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-InstalledQwen {
    $tags = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 30
    $model = @($tags.models) | Where-Object {
        $_.name -eq "qwen3.5:2b" -or $_.model -eq "qwen3.5:2b"
    } | Select-Object -First 1
    return $model
}

function Invoke-QwenPull {
    param([switch]$CancelAfterPartialBlob)

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "http://127.0.0.1:11434/api/pull")
    $request.Content = [System.Net.Http.StringContent]::new(
        '{"model":"qwen3.5:2b","stream":true}',
        [System.Text.Encoding]::UTF8,
        "application/json")
    $cancellation = [System.Threading.CancellationTokenSource]::new()
    $response = $null
    $reader = $null
    $maxCompleted = 0L
    $cancelDigest = $null
    $success = $false

    try {
        $response = $client.SendAsync(
            $request,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead,
            $cancellation.Token).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            $detail = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            throw "Ollama /api/pull returned HTTP $([int]$response.StatusCode): $detail"
        }

        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8)
        while ($true) {
            $line = $reader.ReadLineAsync($cancellation.Token).GetAwaiter().GetResult()
            if ($null -eq $line) { break }
            if ($line.Length -eq 0) { continue }
            if ($line.Length -gt 65536) { throw "Ollama sent a progress record larger than 64 KiB." }

            $record = $line | ConvertFrom-Json
            if ($record.error) { throw "Ollama pull failed: $($record.error)" }

            if ($null -ne $record.completed) {
                $completed = [long]$record.completed
                if ($completed -gt $maxCompleted) { $maxCompleted = $completed }
                if ($CancelAfterPartialBlob -and $completed -ge 8MB -and
                    -not [string]::IsNullOrWhiteSpace([string]$record.digest)) {
                    $cancelDigest = [string]$record.digest
                    $cancellation.Cancel()
                    return [pscustomobject]@{
                        Cancelled = $true
                        CompletedBytes = $completed
                        BlobDigest = $cancelDigest
                        Success = $false
                    }
                }
            }
            if ($record.status -eq "success") { $success = $true }
        }

        if ($CancelAfterPartialBlob) {
            throw "The model pull ended before a partial blob reached the cancellation threshold."
        }
        if (-not $success) { throw "Ollama pull stream ended without a success status." }
        return [pscustomobject]@{
            Cancelled = $false
            CompletedBytes = $maxCompleted
            BlobDigest = $null
            Success = $true
        }
    }
    catch [System.OperationCanceledException] {
        if ($CancelAfterPartialBlob) {
            return [pscustomobject]@{
                Cancelled = $true
                CompletedBytes = $maxCompleted
                BlobDigest = $cancelDigest
                Success = $false
            }
        }
        throw
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
        $cancellation.Dispose()
    }
}

function Invoke-QwenChatHealth {
    $body = @{
        model = "qwen3.5:2b"
        messages = @(@{ role = "user"; content = "Reply with the single word READY." })
        stream = $false
        options = @{ num_predict = 16 }
    } | ConvertTo-Json -Depth 8
    $reply = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:11434/api/chat" -ContentType "application/json" -Body $body -TimeoutSec 180
    if ([string]::IsNullOrWhiteSpace([string]$reply.message.content)) {
        throw "Qwen local chat health check returned an empty response."
    }
}

try {
    Get-Process -Name ollama -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $portDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $portDeadline -and
           (Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue) {
        throw "Port 11434 is still in use. The isolated lifecycle test refuses to reuse another server."
    }

    $server = Start-Process -FilePath $OllamaExePath -ArgumentList @("serve") -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    $serverReady = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($server.HasExited) { throw "The isolated Ollama server exited during startup. See $stderrPath" }
        try {
            $null = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:11434/api/version" -TimeoutSec 3
            $serverReady = $true
            break
        }
        catch { Start-Sleep -Seconds 2 }
    }
    if (-not $serverReady) { throw "The isolated Ollama server did not start within two minutes." }

    $keyPath = Join-Path $env:USERPROFILE ".ollama/id_ed25519"
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        throw "Ollama did not initialize its expected Ed25519 user key at $keyPath."
    }

    # Validate the actual key created by the Windows Ollama runtime against the live Registry.
    $env:LAYAL_RUN_REGISTRY_LIVE_TESTS = "1"
    $env:LAYAL_OLLAMA_PROFILE_DIRECTORY = $env:USERPROFILE
    dotnet test tests/Layal.Tests/Layal.Tests.csproj --configuration Release --nologo --filter "FullyQualifiedName~LiveRegistryManifestAccessAndOptionalOllamaSignatureChallenge"
    if ($LASTEXITCODE -ne 0) { throw "Live Ollama Registry manifest/authentication test failed." }
    $summary.RegistryAuthTest = "passed; test output records anonymous versus signed Bearer mode"

    $firstAttempt = Invoke-QwenPull -CancelAfterPartialBlob
    if (-not $firstAttempt.Cancelled -or $firstAttempt.CompletedBytes -lt 8MB) {
        throw "The cancellation run did not interrupt a real partially downloaded model blob."
    }
    $summary.CancelledAfterBytes = [long]$firstAttempt.CompletedBytes

    # Reissue the pull and prove progress resumes from the interrupted blob's retained bytes.
    $retryRequest = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "http://127.0.0.1:11434/api/pull")
    $retryRequest.Content = [System.Net.Http.StringContent]::new(
        '{"model":"qwen3.5:2b","stream":true}',
        [System.Text.Encoding]::UTF8,
        "application/json")
    $retryResponse = $client.SendAsync(
        $retryRequest,
        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    if (-not $retryResponse.IsSuccessStatusCode) {
        throw "The resumed Ollama pull returned HTTP $([int]$retryResponse.StatusCode)."
    }
    $retryStream = $retryResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $retryReader = [System.IO.StreamReader]::new($retryStream, [System.Text.Encoding]::UTF8)
    $retryCancellation = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(55))
    $retryCompleted = 0L
    $resumeObserved = $false
    $pullSuccess = $false
    $retryDeadline = [DateTimeOffset]::UtcNow.AddMinutes(55)
    try {
        while ([DateTimeOffset]::UtcNow -lt $retryDeadline) {
            $line = $retryReader.ReadLineAsync($retryCancellation.Token).GetAwaiter().GetResult()
            if ($null -eq $line) { break }
            if ($line.Length -eq 0) { continue }
            $record = $line | ConvertFrom-Json
            if ($record.error) { throw "Resumed Ollama pull failed: $($record.error)" }
            if ($null -ne $record.completed) {
                $completed = [long]$record.completed
                if ($completed -gt $retryCompleted) { $retryCompleted = $completed }
                if ($record.digest -eq $firstAttempt.BlobDigest -and
                    $completed -ge $firstAttempt.CompletedBytes) {
                    $resumeObserved = $true
                }
            }
            if ($record.status -eq "success") { $pullSuccess = $true; break }
        }
    }
    finally {
        $retryCancellation.Dispose()
        $retryReader.Dispose()
        $retryResponse.Dispose()
        $retryRequest.Dispose()
    }
    if (-not $pullSuccess) { throw "The resumed model pull did not complete before the 55-minute deadline." }
    if (-not $resumeObserved) { throw "The retry did not report continued progress for the previously interrupted blob." }

    $summary.ResumedFromBytes = [long]$firstAttempt.CompletedBytes
    $installed = Get-InstalledQwen
    if ($null -eq $installed -or $installed.digest -notmatch '^[0-9a-fA-F]{64}$') {
        throw "The installed Qwen model did not report a complete SHA-256 digest via /api/tags."
    }
    $summary.FinalDigest = [string]$installed.digest

    Invoke-QwenChatHealth
    $summary.ChatHealth = "passed"

    $backupAlias = "layal-rollback:phase-c-acceptance"
    Invoke-OllamaCli -Arguments @("cp", "qwen3.5:2b", $backupAlias)
    $backup = @((Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:11434/api/tags").models) |
        Where-Object { $_.name -eq $backupAlias -or $_.model -eq $backupAlias } |
        Select-Object -First 1
    if ($null -eq $backup -or $backup.digest -ne $installed.digest) {
        throw "The isolated rollback copy did not preserve the original model digest."
    }

    # Simulate a failed update by removing the mutable tag, then restore it from the rollback alias.
    Invoke-OllamaCli -Arguments @("rm", "qwen3.5:2b")
    $removed = Get-InstalledQwen
    if ($null -ne $removed) { throw "The original tag still exists after the rollback test removed it." }

    Invoke-OllamaCli -Arguments @("cp", $backupAlias, "qwen3.5:2b")
    $restored = Get-InstalledQwen
    if ($null -eq $restored -or $restored.digest -ne $installed.digest) {
        throw "Ollama rollback copy did not restore the exact previous model digest."
    }
    Invoke-QwenChatHealth
    $summary.Rollback = "passed: restored exact digest and reran chat health check"
    Invoke-OllamaCli -Arguments @("rm", $backupAlias)

    $blobDirectory = Join-Path $modelDirectory "blobs"
    if (Test-Path -LiteralPath $blobDirectory -PathType Container) {
        $summary.DownloadedBlobBytes = [long]((Get-ChildItem -LiteralPath $blobDirectory -File -Recurse | Measure-Object -Property Length -Sum).Sum)
    }
    $summary.Result = "passed"
}
finally {
    $client.Dispose()
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $server.Id -Timeout 15 -ErrorAction SilentlyContinue
    }
    $summary.CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory "qwen-lifecycle-summary.json") -Encoding utf8
}

if ($summary.Result -ne "passed") {
    throw "Qwen model lifecycle acceptance failed. Evidence: $EvidenceDirectory"
}
Write-Host "Qwen model lifecycle acceptance: PASS"
Get-Content -LiteralPath (Join-Path $EvidenceDirectory "qwen-lifecycle-summary.json")
