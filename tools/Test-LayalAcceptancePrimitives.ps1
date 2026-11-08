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

Write-Host 'PowerShell acceptance primitive tests: PASS (success, false return, exception, no-output).'
exit 0
