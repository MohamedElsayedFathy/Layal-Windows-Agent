[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\src\Layal.Desktop\tessdata')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Model files are pinned to a single reviewed upstream commit. These Git blob IDs
# are calculated over the canonical Git object header plus the exact file bytes.
$UpstreamRepository = 'tesseract-ocr/tessdata_fast'
$UpstreamCommit = '87416418657359cb625c412a48b6e1d6d41c29bd'
$Models = @(
    @{
        Name = 'ara.traineddata'
        Length = 1432056
        GitBlobSha = 'c8d129c67821c1592cac46836686b057ecc203dc'
    },
    @{
        Name = 'eng.traineddata'
        Length = 4113088
        GitBlobSha = 'bbef4675053b5b468cdb477053e28b1c698ba08e'
    }
)

function Get-GitBlobSha1 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    $header = [Text.Encoding]::UTF8.GetBytes("blob $($bytes.Length)`0")
    $payload = [byte[]]::new($header.Length + $bytes.Length)
    [Buffer]::BlockCopy($header, 0, $payload, 0, $header.Length)
    [Buffer]::BlockCopy($bytes, 0, $payload, $header.Length, $bytes.Length)

    $algorithm = [Security.Cryptography.SHA1]::Create()
    try {
        $digest = $algorithm.ComputeHash($payload)
        return ([Convert]::ToHexString($digest)).ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
        [Array]::Clear($payload, 0, $payload.Length)
    }
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

foreach ($model in $Models) {
    $targetPath = Join-Path $Destination $model.Name
    $validExisting = $false
    if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
        $existingLength = (Get-Item -LiteralPath $targetPath).Length
        if ($existingLength -eq $model.Length) {
            $existingSha = Get-GitBlobSha1 -Path $targetPath
            $validExisting = $existingSha -eq $model.GitBlobSha
        }
    }

    if ($validExisting) {
        Write-Host "Verified existing OCR model: $($model.Name)"
        continue
    }

    Remove-Item -LiteralPath $targetPath -Force -ErrorAction SilentlyContinue
    $temporaryPath = "$targetPath.download"
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    $url = "https://raw.githubusercontent.com/$UpstreamRepository/$UpstreamCommit/$($model.Name)"
    Write-Host "Downloading pinned local OCR model: $($model.Name)"
    Invoke-WebRequest -Uri $url -OutFile $temporaryPath -UseBasicParsing

    $actualLength = (Get-Item -LiteralPath $temporaryPath).Length
    if ($actualLength -ne $model.Length) {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        throw "OCR model $($model.Name) has an unexpected size: $actualLength (expected $($model.Length))."
    }

    $actualSha = Get-GitBlobSha1 -Path $temporaryPath
    if ($actualSha -ne $model.GitBlobSha) {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        throw "OCR model $($model.Name) failed its pinned Git blob identity check."
    }

    Move-Item -LiteralPath $temporaryPath -Destination $targetPath -Force
    Write-Host "PASS: $($model.Name) size and pinned Git blob identity verified."
}

foreach ($requiredModel in @('ara.traineddata', 'eng.traineddata')) {
    $requiredPath = Join-Path $Destination $requiredModel
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required OCR model was not installed: $requiredPath"
    }
}

Write-Host 'PASS: Arabic and English offline OCR models are ready.'
