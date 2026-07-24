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
    [switch]$VerifyBundleOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-SafeBundleFile {
    param(
        [Parameter(Mandatory)][string]$BundleRoot,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    if ([IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Split(
            [char[]]@("/", "\"),
            [StringSplitOptions]::None) -contains "..") {
        throw "Unsafe bundle path: $RelativePath"
    }

    $rootWithSeparator = [IO.Path]::GetFullPath($BundleRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $BundleRoot $RelativePath))
    if (-not $path.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Bundle path escapes its root: $RelativePath"
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Bundle file is missing: $RelativePath"
    }

    $actual = Get-FileSha256 -Path $path
    if (-not [string]::Equals(
            $actual,
            $ExpectedSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 mismatch for ${RelativePath}: expected $ExpectedSha256, got $actual"
    }
    return $path
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter()][string[]]$Arguments = @()
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
    }
}

$bundleRoot = $PSScriptRoot
$manifestPath = Join-Path $bundleRoot "bundle-manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "bundle-manifest.json was not found beside this installer."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.formatVersion -ne 1) {
    throw "Unsupported bundle manifest version: $($manifest.formatVersion)"
}
if ($manifest.runtimeIdentifier -ne "win-x64") {
    throw "This installer supports the win-x64 full bundle only."
}
if ($manifest.model.cacheDirectoryName -notmatch "^[A-Za-z0-9._-]+$") {
    throw "Unsafe model cache directory name in bundle manifest."
}

$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows)
$processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
if (-not $VerifyBundleOnly -and
    (-not $runningOnWindows -or
     $processArchitecture -ne [Runtime.InteropServices.Architecture]::X64)) {
    throw "This full offline bundle requires 64-bit Windows and an x64 PowerShell process."
}

$verifiedPackages = @()
foreach ($package in $manifest.packages) {
    $verifiedPackages += Assert-SafeBundleFile `
        -BundleRoot $bundleRoot `
        -RelativePath $package.path `
        -ExpectedSha256 $package.sha256
}

$verifiedModelFiles = @()
foreach ($file in $manifest.model.files) {
    if ($file.name -notmatch "^[A-Za-z0-9._-]+$") {
        throw "Unsafe model file name in bundle manifest: $($file.name)"
    }
    $verifiedModelFiles += [pscustomobject]@{
        Name = $file.name
        Path = Assert-SafeBundleFile `
            -BundleRoot $bundleRoot `
            -RelativePath $file.path `
            -ExpectedSha256 $file.sha256
        Sha256 = $file.sha256
    }
}

if ($VerifyBundleOnly) {
    Write-Host (
        "Full offline bundle verified: tool {0}, model {1}, {2} package(s), {3} model file(s)." -f
        $manifest.toolVersion,
        $manifest.model.id,
        $verifiedPackages.Count,
        $verifiedModelFiles.Count)
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found on PATH. Install the .NET 10 SDK first."
}

$packageDirectory = Join-Path $bundleRoot "packages"
$nugetConfigPath = Join-Path $bundleRoot "NuGet.offline.config"
[xml]$nugetConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="sourcegraph-full-bundle" value="" />
  </packageSources>
</configuration>
'@
$nugetConfig.configuration.packageSources.add.SetAttribute(
    "value",
    $packageDirectory)
$nugetConfig.Save($nugetConfigPath)

$globalTools = (& dotnet tool list --global 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect global .NET tools.`n$globalTools"
}
$toolInstalled = $globalTools -match "(?im)^\s*DevBitsLab\.Mcp\.SourceGraph\.Tool\s+"
$verb = if ($toolInstalled) { "update" } else { "install" }
Write-Host "$($verb.Substring(0, 1).ToUpperInvariant() + $verb.Substring(1))ing SourceGraph MCP $($manifest.toolVersion) from the offline bundle..."
Invoke-CheckedCommand -Command "dotnet" -Arguments @(
    "tool", $verb, "--global",
    $manifest.toolPackageId,
    "--version", $manifest.toolVersion,
    "--configfile", $nugetConfigPath
)

$userProfile = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::UserProfile)
$dotnetTools = Join-Path $userProfile ".dotnet\tools"
$env:PATH = "$dotnetTools$([IO.Path]::PathSeparator)$env:PATH"
if (-not (Get-Command sourcegraph-mcp -ErrorAction SilentlyContinue)) {
    throw "The tool was installed but sourcegraph-mcp is unavailable on PATH."
}

$localAppData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localAppData)) {
    throw "Could not resolve the Windows local application-data directory."
}
$modelDirectory = Join-Path (
    Join-Path $localAppData "devbitslab.sourcegraph\models"
) $manifest.model.cacheDirectoryName
[IO.Directory]::CreateDirectory($modelDirectory) | Out-Null

Write-Host "Installing and verifying the bundled embedding model..."
foreach ($file in $verifiedModelFiles) {
    $destination = Join-Path $modelDirectory $file.Name
    if ((Test-Path -LiteralPath $destination -PathType Leaf) -and
        (Get-FileSha256 -Path $destination) -eq $file.Sha256) {
        continue
    }

    $temporary = "$destination.install-$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Copy-Item -LiteralPath $file.Path -Destination $temporary
        $copiedHash = Get-FileSha256 -Path $temporary
        if ($copiedHash -ne $file.Sha256) {
            throw "SHA-256 mismatch after copying $($file.Name)."
        }
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            [IO.File]::Replace($temporary, $destination, $null, $true)
        }
        else {
            [IO.File]::Move($temporary, $destination)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

Invoke-CheckedCommand -Command "sourcegraph-mcp" -Arguments @(
    "embeddings", "verify"
)

$setupScript = Join-Path $bundleRoot "setup-sourcegraph-mcp.ps1"
if (-not (Test-Path -LiteralPath $setupScript -PathType Leaf)) {
    throw "setup-sourcegraph-mcp.ps1 is missing from the bundle."
}
$setupArguments = @{
    ProjectRoot = $ProjectRoot
}
if (-not [string]::IsNullOrWhiteSpace($Solution)) {
    $setupArguments.Solution = $Solution
}
if ($SkipPrewarm) {
    $setupArguments.SkipPrewarm = $true
}
& $setupScript @setupArguments
if ($LASTEXITCODE -ne 0) {
    throw "Project setup failed with exit code $LASTEXITCODE."
}
