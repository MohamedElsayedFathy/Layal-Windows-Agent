[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runnerPath = Join-Path $root 'tools/Invoke-LayalFinalAcceptance.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($runnerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "The final-acceptance runner has PowerShell syntax errors: $($parseErrors[0].Message)"
}

$functionAst = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Invoke-Check'
}, $true)
if ($null -eq $functionAst) {
    throw 'Could not locate the real Invoke-Check function in Invoke-LayalFinalAcceptance.ps1.'
}

$script:CapturedAcceptanceResults = [System.Collections.Generic.List[object]]::new()
function Add-CheckResult {
    param(
        [string]$Name,
        [string]$Category,
        [string]$Status,
        [string]$Details,
        [double]$DurationSeconds
    )
    $script:CapturedAcceptanceResults.Add([pscustomobject]@{
        Name = $Name
        Category = $Category
        Status = $Status
        Details = $Details
        DurationSeconds = $DurationSeconds
    }) | Out-Null
}

. ([scriptblock]::Create($functionAst.Extent.Text))

$passResult = Invoke-Check -Name 'self-test pass' -Category 'Self-test' -Action { 'expected success' }
if ($passResult -ne $true -or $script:CapturedAcceptanceResults[-1].Status -ne 'PASS') {
    throw 'Invoke-Check must report a successful action as PASS.'
}

$falseResult = Invoke-Check -Name 'self-test false' -Category 'Self-test' -Action { return $false }
if ($falseResult -ne $false -or $script:CapturedAcceptanceResults[-1].Status -ne 'FAIL') {
    throw 'Invoke-Check incorrectly accepted a Boolean false result.'
}
if ($script:CapturedAcceptanceResults[-1].Details -notmatch 'Boolean false') {
    throw 'The Boolean false failure must explain why the acceptance action failed.'
}

$throwResult = Invoke-Check -Name 'self-test exception' -Category 'Self-test' -Action { throw 'expected test exception' }
if ($throwResult -ne $false -or $script:CapturedAcceptanceResults[-1].Status -ne 'FAIL') {
    throw 'Invoke-Check must report a thrown exception as FAIL.'
}

$emptyResult = Invoke-Check -Name 'self-test no-output' -Category 'Self-test' -Action { return }
if ($emptyResult -ne $true -or $script:CapturedAcceptanceResults[-1].Status -ne 'PASS') {
    throw 'Invoke-Check must continue to accept a successful action that emits no detail.'
}

$childFunctionAst = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Invoke-PowerShellChild'
}, $true)
if ($null -eq $childFunctionAst) {
    throw 'Could not locate Invoke-PowerShellChild in the final-acceptance runner.'
}
. ([scriptblock]::Create($childFunctionAst.Extent.Text))

$script:PowerShell = Get-Command 'pwsh' -ErrorAction Stop
$script:Root = $root
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ("Layal-PowerShell-Primitive-" + [Guid]::NewGuid().ToString('N'))
$probeScript = Join-Path $probeRoot 'empty-args-child.ps1'
$probeLogs = Join-Path $probeRoot 'logs'
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
try {
    [IO.File]::WriteAllText(
        $probeScript,
        "Write-Output 'child-empty-args-ok'",
        [Text.UTF8Encoding]::new($false))
    $childResult = Invoke-PowerShellChild -Name 'empty-arguments child process self-test' -ScriptPath $probeScript -Arguments @() -LogDirectory $probeLogs
    $childOutput = Get-Content -LiteralPath $childResult.StdoutPath -Raw
    if ($childResult.ExitCode -ne 0 -or $childOutput -notmatch 'child-empty-args-ok') {
        throw 'Invoke-PowerShellChild did not execute a script successfully with an empty argument list.'
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'PowerShell acceptance primitive tests: PASS (success, false return, exception, no-output, empty child arguments).'
exit 0
