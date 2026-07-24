[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [string]$ModelDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [Parameter()]
    [ValidateSet("win-x64")]
    [string]$RuntimeIdentifier = "win-x64",

    [Parameter()]
    [string]$ModelManifest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\.."))
$packageDirectoryPath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$modelDirectoryPath = (Resolve-Path -LiteralPath $ModelDirectory).Path
[IO.Directory]::CreateDirectory(
    [IO.Path]::GetFullPath($OutputDirectory)) | Out-Null
$outputDirectoryPath = (Resolve-Path -LiteralPath $OutputDirectory).Path

if ([string]::IsNullOrWhiteSpace($ModelManifest)) {
    $ModelManifest = Join-Path $repositoryRoot "distribution\full\model-manifest.json"
}
$modelManifestPath = (Resolve-Path -LiteralPath $ModelManifest).Path
$model = Get-Content -LiteralPath $modelManifestPath -Raw | ConvertFrom-Json
if ($model.formatVersion -ne 1) {
    throw "Unsupported model manifest version: $($model.formatVersion)"
}

$outerPattern =
    "^DevBitsLab\.Mcp\.SourceGraph\.Tool\.(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\.nupkg$"
$outerPackages = @(
    Get-ChildItem -LiteralPath $packageDirectoryPath -File |
        Where-Object { $_.Name -match $outerPattern }
)
if ($outerPackages.Count -ne 1) {
    throw "Expected exactly one outer SourceGraph MCP tool package; found $($outerPackages.Count)."
}
$null = $outerPackages[0].Name -match $outerPattern
$toolVersion = $Matches.version
$implementationName =
    "DevBitsLab.Mcp.SourceGraph.Tool.$RuntimeIdentifier.$toolVersion.nupkg"
$implementationPath = Join-Path $packageDirectoryPath $implementationName
if (-not (Test-Path -LiteralPath $implementationPath -PathType Leaf)) {
    throw "Required runtime package is missing: $implementationName"
}

$verifiedModelFiles = @()
foreach ($file in $model.files) {
    if ($file.name -notmatch "^[A-Za-z0-9._-]+$") {
        throw "Unsafe model file name: $($file.name)"
    }
    $path = Join-Path $modelDirectoryPath $file.name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Model file is missing: $path"
    }
    $actual = Get-FileSha256 -Path $path
    if ($actual -ne $file.sha256) {
        throw "SHA-256 mismatch for $($file.name): expected $($file.sha256), got $actual"
    }
    $verifiedModelFiles += [pscustomobject]@{
        Name = $file.name
        Path = $path
        Sha256 = $actual
    }
}

$stageRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "sourcegraph-full-bundle-" + [Guid]::NewGuid().ToString("N"))
$archiveName = "SourceGraph-MCP-Full-$RuntimeIdentifier-v$toolVersion.zip"
$archivePath = Join-Path $outputDirectoryPath $archiveName

try {
    $packagesStage = Join-Path $stageRoot "packages"
    $modelStage = Join-Path $stageRoot "model"
    [IO.Directory]::CreateDirectory($packagesStage) | Out-Null
    [IO.Directory]::CreateDirectory($modelStage) | Out-Null

    $packageFiles = @($outerPackages[0], (Get-Item -LiteralPath $implementationPath))
    $bundlePackages = @()
    foreach ($package in $packageFiles) {
        $destination = Join-Path $packagesStage $package.Name
        Copy-Item -LiteralPath $package.FullName -Destination $destination
        $bundlePackages += [ordered]@{
            path = "packages/$($package.Name)"
            sha256 = Get-FileSha256 -Path $destination
        }
    }

    $bundleModelFiles = @()
    foreach ($file in $verifiedModelFiles) {
        $destination = Join-Path $modelStage $file.Name
        Copy-Item -LiteralPath $file.Path -Destination $destination
        $bundleModelFiles += [ordered]@{
            name = $file.Name
            path = "model/$($file.Name)"
            sha256 = $file.Sha256
        }
    }

    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot "distribution\full\install-sourcegraph-mcp.ps1"
    ) -Destination (Join-Path $stageRoot "install-sourcegraph-mcp.ps1")
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot "setup-sourcegraph-mcp.ps1"
    ) -Destination (Join-Path $stageRoot "setup-sourcegraph-mcp.ps1")
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot "distribution\full\THIRD-PARTY-NOTICES.txt"
    ) -Destination (Join-Path $stageRoot "THIRD-PARTY-NOTICES.txt")
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot "distribution\full\APACHE-2.0.txt"
    ) -Destination (Join-Path $stageRoot "APACHE-2.0.txt")
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot "distribution\full\README-zh-CN.txt"
    ) -Destination (Join-Path $stageRoot "README-zh-CN.txt")
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot "LICENSE"
    ) -Destination (Join-Path $stageRoot "PROJECT-LICENSE.txt")

    $bundleManifest = [ordered]@{
        formatVersion = 1
        toolPackageId = "DevBitsLab.Mcp.SourceGraph.Tool"
        toolVersion = $toolVersion
        runtimeIdentifier = $RuntimeIdentifier
        packages = $bundlePackages
        model = [ordered]@{
            id = $model.modelId
            cacheDirectoryName = $model.cacheDirectoryName
            license = $model.license
            sourceUrl = $model.sourceUrl
            files = $bundleModelFiles
        }
    }
    $bundleManifest |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (
            Join-Path $stageRoot "bundle-manifest.json"
        ) -Encoding utf8

    & (Join-Path $stageRoot "install-sourcegraph-mcp.ps1") -VerifyBundleOnly

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $stageRoot,
        $archivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    $archiveHash = Get-FileSha256 -Path $archivePath
    "$archiveHash  $archiveName" |
        Set-Content -LiteralPath "$archivePath.sha256" -Encoding ascii
    Write-Host "Created full offline bundle: $archivePath"
    Write-Host "SHA-256: $archiveHash"
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
