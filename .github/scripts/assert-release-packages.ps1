[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,

    [Parameter(Mandatory)]
    [string] $ToolVersion,

    [Parameter(Mandatory)]
    [string] $SdkVersion,

    [Parameter(Mandatory)]
    [string] $ToolRuntimeIdentifiers
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$toolPackageId = 'DevBitsLab.Mcp.SourceGraph.Tool'
$sdkPackageId = 'DevBitsLab.Mcp.SourceGraph.Sdk'
$packageDirectoryPath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$releaseVersionPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'

if ($ToolVersion -notmatch $releaseVersionPattern) {
    throw "Tool version '$ToolVersion' is not a release version."
}
if ($SdkVersion -notmatch $releaseVersionPattern) {
    throw "SDK version '$SdkVersion' is not a release version."
}

$runtimeIdentifiers = @(
    $ToolRuntimeIdentifiers.Split(
        ';',
        [StringSplitOptions]::RemoveEmptyEntries -bor
            [StringSplitOptions]::TrimEntries)
)
if ($runtimeIdentifiers.Count -eq 0) {
    throw 'The tool does not declare any runtime identifiers.'
}
if (@($runtimeIdentifiers | Sort-Object -Unique).Count -ne
    $runtimeIdentifiers.Count) {
    throw 'The tool declares duplicate runtime identifiers.'
}

$expectedVersions = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$expectedVersions.Add($toolPackageId, $ToolVersion)
foreach ($runtimeIdentifier in $runtimeIdentifiers) {
    $expectedVersions.Add(
        "$toolPackageId.$runtimeIdentifier",
        $ToolVersion)
}
$expectedVersions.Add($sdkPackageId, $SdkVersion)

$packages = @(
    Get-ChildItem -File -LiteralPath $packageDirectoryPath |
        Where-Object Extension -CEQ '.nupkg'
)
if ($packages.Count -ne $expectedVersions.Count) {
    throw (
        "Expected $($expectedVersions.Count) release packages in " +
        "$packageDirectoryPath; found $($packages.Count).")
}

$actualPackageIds = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($package in $packages) {
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $nuspecEntries = @(
            $archive.Entries |
                Where-Object FullName -Like '*.nuspec'
        )
        if ($nuspecEntries.Count -ne 1) {
            throw (
                "Package $($package.Name) contains " +
                "$($nuspecEntries.Count) nuspec files.")
        }

        $reader = [IO.StreamReader]::new($nuspecEntries[0].Open())
        try {
            [xml] $nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $metadata = @(
            $nuspec.GetElementsByTagName('metadata')
        )
        if ($metadata.Count -ne 1) {
            throw "Package $($package.Name) has invalid nuspec metadata."
        }
        $id = [string] $metadata[0].GetElementsByTagName('id')[0].InnerText
        $version =
            [string] $metadata[0].GetElementsByTagName('version')[0].InnerText
    }
    finally {
        $archive.Dispose()
    }

    if (-not $expectedVersions.ContainsKey($id)) {
        throw "Unexpected package '$id' in $($package.Name)."
    }
    if (-not $actualPackageIds.Add($id)) {
        throw "Duplicate package '$id' in $packageDirectoryPath."
    }

    $expectedVersion = $expectedVersions[$id]
    if ($version -cne $expectedVersion) {
        throw (
            "Package '$id' has version '$version'; expected " +
            "'$expectedVersion'.")
    }
    $expectedFileName = "$id.$expectedVersion.nupkg"
    if ($package.Name -cne $expectedFileName) {
        throw (
            "Package '$id' has file name '$($package.Name)'; expected " +
            "'$expectedFileName'.")
    }
}

$missingPackageIds = @(
    $expectedVersions.Keys |
        Where-Object { -not $actualPackageIds.Contains($_) }
)
if ($missingPackageIds.Count -gt 0) {
    throw "Missing release packages: $($missingPackageIds -join ', ')."
}

Write-Host (
    "Verified tool $ToolVersion for $($runtimeIdentifiers.Count) runtimes " +
    "and SDK $SdkVersion across $($packages.Count) packages.")
