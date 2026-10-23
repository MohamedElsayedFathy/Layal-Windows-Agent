[CmdletBinding()]
param(
    [switch]$Apply,
    [ValidateRange(1, 6)]
    [int]$RequiredApprovingReviews = 1
)

$ErrorActionPreference = 'Stop'
$repository = 'MohamedElsayedFathy/Layal-Windows-Agent'
$branch = 'main'
$target = "$repository/$branch"

$gh = Get-Command 'gh' -ErrorAction SilentlyContinue
if (-not $gh) {
    throw 'GitHub CLI (gh) was not found. Install it from https://cli.github.com/ and authenticate with a repository-admin account.'
}

& $gh.Source auth status --hostname github.com
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub CLI is not authenticated. Run gh auth login with an account that has Admin permission for this repository.'
}

$repoJson = & $gh.Source api "repos/$repository"
if ($LASTEXITCODE -ne 0) { throw 'Could not read repository metadata.' }
$repoInfo = $repoJson | ConvertFrom-Json
if ($repoInfo.default_branch -ne $branch) {
    throw "Safety check failed: expected default branch '$branch', received '$($repoInfo.default_branch)'."
}
if (-not $repoInfo.permissions.admin) {
    throw "The authenticated account does not report Admin permission for $repository."
}

$policy = @{
    required_status_checks = @{
        strict = $true
        contexts = @(
            'build',
            'Scan Git commit range for exposed secrets',
            'Audit NuGet advisories and license inventory'
        )
    }
    enforce_admins = $true
    required_pull_request_reviews = @{
        dismiss_stale_reviews = $true
        require_code_owner_reviews = $false
        required_approving_review_count = $RequiredApprovingReviews
        require_last_push_approval = $true
    }
    restrictions = $null
    required_linear_history = $true
    allow_force_pushes = $false
    allow_deletions = $false
    block_creations = $false
    required_conversation_resolution = $true
    lock_branch = $false
    allow_fork_syncing = $true
}
$body = $policy | ConvertTo-Json -Depth 8

Write-Host "Target repository/branch: $target"
Write-Host 'Policy preview:'
Write-Host $body

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN ONLY: no settings changed. Re-run with -Apply to request the protection update.'
    return
}

$confirmation = Read-Host "To apply the branch protection policy, type exactly '$target'"
if ($confirmation -cne $target) {
    throw 'Confirmation did not match. No branch protection settings were changed.'
}

$body | & $gh.Source api --method PUT "repos/$repository/branches/$branch/protection" --input - --header 'Accept: application/vnd.github+json' --header 'X-GitHub-Api-Version: 2022-11-28' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub rejected the protection update. Check repository-plan support and Admin permission; no successful result is claimed.'
}

$verifyJson = & $gh.Source api "repos/$repository/branches/$branch/protection"
if ($LASTEXITCODE -ne 0) { throw 'Update returned success but the protection settings could not be read back.' }
$verify = $verifyJson | ConvertFrom-Json
$expectedContexts = @(
    'build',
    'Scan Git commit range for exposed secrets',
    'Audit NuGet advisories and license inventory'
)
$actualContexts = @($verify.required_status_checks.contexts)
$missingContexts = @($expectedContexts | Where-Object { $_ -notin $actualContexts })
$resolutionEnabled = ($verify.required_conversation_resolution -eq $true) -or ($verify.required_conversation_resolution.enabled -eq $true)
$adminsEnabled = ($verify.enforce_admins -eq $true) -or ($verify.enforce_admins.enabled -eq $true)
$reviews = $verify.required_pull_request_reviews
if ($missingContexts.Count -gt 0 -or -not $verify.protected -or -not $adminsEnabled -or -not $resolutionEnabled -or
    $reviews.required_approving_review_count -lt $RequiredApprovingReviews -or
    -not $verify.required_status_checks.strict -or
    $verify.allow_force_pushes.enabled -eq $true -or
    $verify.allow_deletions.enabled -eq $true) {
    throw "Read-back did not match the requested policy. Missing contexts: $($missingContexts -join ', '). Inspect GitHub branch protection settings manually."
}

Write-Host "PASS: branch protection is active and verified for $target."
Write-Host "Required checks: $($expectedContexts -join '; ')"
Write-Host "Required approvals: $($reviews.required_approving_review_count)"
