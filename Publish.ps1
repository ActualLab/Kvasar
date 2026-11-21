# Publishes the NuGet package by running .github/workflows/Publish.yml on GitHub,
# which authenticates via trusted publishing, so no API keys are needed here.
# The workflow publishes origin/main, so the local main must match it.
# -NoTest (or -no-test) skips the workflow's tests.
param(
    [Alias('no-test')]
    [switch] $NoTest
)

$ErrorActionPreference = 'Stop'
# GH_TOKEN may be a fine-grained token of another organization, which can't start workflows here
if ($env:GH_TOKEN_FUSION) {
    $env:GH_TOKEN = $env:GH_TOKEN_FUSION
}
Push-Location $PSScriptRoot
try {
    $branch = & git rev-parse --abbrev-ref HEAD
    if ($branch -ne 'main') {
        throw "Publishing is possible only from main, but the current branch is '$branch'."
    }
    if (& git status --porcelain) {
        throw "The working tree has uncommitted changes."
    }

    & git fetch -q origin main
    $sha = & git rev-parse HEAD
    $remoteSha = & git rev-parse origin/main
    if ($sha -ne $remoteSha) {
        throw "main ($sha) doesn't match origin/main ($remoteSha): push or pull first."
    }

    & dotnet tool restore | Out-Null
    $version = & dotnet nbgv get-version -v NuGetPackageVersion
    if ($LASTEXITCODE -ne 0 -or -not $version) {
        throw "Failed to get version from nbgv."
    }
    Write-Host "Publishing $version from $sha$(if ($NoTest) { ' (no tests)' })"

    $startedAt = [DateTimeOffset]::UtcNow.AddSeconds(-10)
    & gh workflow run Publish.yml --ref main -f "skip-tests=$($NoTest.IsPresent.ToString().ToLowerInvariant())"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start the Publish workflow."
    }

    # "gh workflow run" doesn't report the run it starts, so it's found by its commit and start time
    $runId = $null
    for ($i = 0; $i -lt 30 -and -not $runId; $i++) {
        Start-Sleep -Seconds 2
        $runs = & gh run list --workflow Publish.yml --event workflow_dispatch --branch main --limit 10 `
            --json databaseId,headSha,createdAt | ConvertFrom-Json
        $runId = $runs |
            Where-Object { $_.headSha -eq $sha -and ([DateTimeOffset]$_.createdAt) -ge $startedAt } |
            Select-Object -First 1 -ExpandProperty databaseId
    }
    if (-not $runId) {
        throw "The started Publish workflow run isn't found."
    }

    # The run waits for a "release" environment reviewer to approve it on this page
    $runUrl = & gh run view $runId --json url --jq .url
    Write-Host "Approve the run to start publishing: $runUrl"
    & gh run view $runId --web

    & gh run watch $runId --exit-status --interval 15
    if ($LASTEXITCODE -ne 0) {
        & gh run view $runId --log-failed
        throw "The Publish workflow run failed: $(& gh run view $runId --json url --jq .url)"
    }

    Write-Host "Published $version."
}
finally {
    Pop-Location
}
