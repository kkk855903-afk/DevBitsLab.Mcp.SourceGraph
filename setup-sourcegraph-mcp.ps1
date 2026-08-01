#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ProjectRoot = (Get-Location).Path,

    [Parameter()]
    [string]$Solution,

    [Parameter()]
    [switch]$SkipPrewarm,

    [Parameter()]
    [switch]$PullEmbeddings,

    [Parameter()]
    [switch]$NoEmbeddings
)

$ErrorActionPreference = "Stop"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter()]
        [string[]]$Arguments = @()
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
    }
}

function Resolve-SolutionPath {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter()]
        [string]$RequestedSolution
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedSolution)) {
        $candidate = if ([IO.Path]::IsPathRooted($RequestedSolution)) {
            $RequestedSolution
        }
        else {
            Join-Path $Root $RequestedSolution
        }

        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Solution not found: $candidate"
        }
        return (Resolve-Path -LiteralPath $candidate).Path
    }

    $solutions = @(Get-ChildItem -LiteralPath $Root -File |
        Where-Object { $_.Extension -in @(".slnx", ".sln") })
    $slnx = @($solutions | Where-Object { $_.Extension -eq ".slnx" })
    $preferred = if ($slnx.Count -gt 0) { $slnx } else { $solutions }

    if ($preferred.Count -eq 0) {
        throw "No .slnx or .sln file was found directly under: $Root"
    }
    if ($preferred.Count -gt 1) {
        $names = ($preferred.Name | Sort-Object) -join ", "
        throw "Multiple solutions were found ($names). Re-run with -Solution <path>."
    }

    return $preferred[0].FullName
}

if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) {
    throw "Project root not found: $ProjectRoot"
}

$resolvedRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$resolvedSolution = Resolve-SolutionPath -Root $resolvedRoot -RequestedSolution $Solution
$sourceGraphConfigPath = Join-Path $resolvedRoot ".sourcegraph.json"
$hasSourceGraphConfig = Test-Path -LiteralPath $sourceGraphConfigPath -PathType Leaf

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found on PATH. Install the .NET SDK first."
}

if (-not (Get-Command sourcegraph-mcp -ErrorAction SilentlyContinue)) {
    Write-Host "sourcegraph-mcp is not installed; installing the global .NET tool..."
    Invoke-CheckedCommand -Command "dotnet" -Arguments @(
        "tool", "install", "--global", "DevBitsLab.Mcp.SourceGraph.Tool"
    )

    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $dotnetTools = Join-Path $userProfile ".dotnet\tools"
    $env:PATH = "$dotnetTools$([IO.Path]::PathSeparator)$env:PATH"
}

if (-not (Get-Command sourcegraph-mcp -ErrorAction SilentlyContinue)) {
    throw "sourcegraph-mcp was installed but is still unavailable on PATH. Open a new terminal and run this script again."
}

if ($PullEmbeddings -and $NoEmbeddings) {
    throw "-PullEmbeddings and -NoEmbeddings cannot be used together."
}

if ($PullEmbeddings) {
    Write-Host "Downloading and verifying the embedding model..."
    Invoke-CheckedCommand -Command "sourcegraph-mcp" -Arguments @("embeddings", "pull")
    Invoke-CheckedCommand -Command "sourcegraph-mcp" -Arguments @("embeddings", "verify")
}

$initArguments = @(
    "init",
    "--yes",
    "--client", "codex",
    "--root", $resolvedRoot,
    "--force"
)
# A configured repository owns its scope metadata (including interop settings). Passing
# --solution here would intentionally synthesize an implicit scope and bypass that metadata,
# which makes native interop queries report `interop-not-configured` after the next rebuild.
# Keep --solution only for the zero-config path; configured scopes are pre-warmed explicitly
# below so setup retains the same first-run indexing behavior.
if (-not $hasSourceGraphConfig) {
    $initArguments += @("--solution", $resolvedSolution)
}
$initArguments += if ($SkipPrewarm) { "--no-prewarm" } else { "--prewarm" }
if ($NoEmbeddings) {
    $initArguments += "--no-embeddings"
}

Write-Host "Configuring SourceGraph MCP for:"
Write-Host "  root:     $resolvedRoot"
Write-Host "  solution: $resolvedSolution"
Write-Host "  scope mode: $(if ($hasSourceGraphConfig) { 'configured (.sourcegraph.json)' } else { 'single-solution' })"
Invoke-CheckedCommand -Command "sourcegraph-mcp" -Arguments $initArguments

if (-not $SkipPrewarm) {
    if ($hasSourceGraphConfig) {
        Write-Host "Pre-warming index against $resolvedSolution..."
        $indexArguments = @(
            "index", $resolvedSolution,
            "--root", $resolvedRoot
        )
        if ($NoEmbeddings) {
            $indexArguments += "--no-embeddings"
        }
        Invoke-CheckedCommand -Command "sourcegraph-mcp" -Arguments $indexArguments
    }
    Write-Host "Verifying the generated graph..."
    Invoke-CheckedCommand -Command "sourcegraph-mcp" -Arguments @(
        "demo", "--root", $resolvedRoot
    )
}

Write-Host ""
Write-Host "SourceGraph MCP is ready. Start a new Codex task in this project."
