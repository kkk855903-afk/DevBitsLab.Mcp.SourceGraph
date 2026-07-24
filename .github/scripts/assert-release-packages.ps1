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
$repositoryUrl =
    'https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph.git'
$sourceLinkUrlPattern =
    '^https://raw\.githubusercontent\.com/' +
    'Jak3b0/DevBitsLab\.Mcp\.SourceGraph/[0-9a-fA-F]{40}/\*$'
$packageDirectoryPath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$repositoryRoot = (
    Resolve-Path -LiteralPath (
        [IO.Path]::Combine($PSScriptRoot, '..', '..'))
).Path
$licensePath = Join-Path $repositoryRoot 'LICENSE'
$readmePath = Join-Path $repositoryRoot 'README.md'
$releaseVersionPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'

if ($ToolVersion -notmatch $releaseVersionPattern) {
    throw "Tool version '$ToolVersion' is not a release version."
}
if ($SdkVersion -notmatch $releaseVersionPattern) {
    throw "SDK version '$SdkVersion' is not a release version."
}
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw "Repository license file '$licensePath' does not exist."
}
if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
    throw "Repository readme file '$readmePath' does not exist."
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

if (-not ('ReleasePackagePortablePdbInspector' -as [type])) {
    $portablePdbInspector = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Text;

public static class ReleasePackagePortablePdbInspector
{
    private static readonly Guid SourceLinkKind =
        new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    public static string[] ReadSourceLinkDocuments(Stream source)
    {
        using (var stream = new MemoryStream())
        {
            source.CopyTo(stream);
            stream.Position = 0;

            var documents = new List<string>();
            using (var provider =
                MetadataReaderProvider.FromPortablePdbStream(
                    stream,
                    MetadataStreamOptions.LeaveOpen))
            {
                var reader = provider.GetMetadataReader();
                foreach (var handle in reader.CustomDebugInformation)
                {
                    var information =
                        reader.GetCustomDebugInformation(handle);
                    if (reader.GetGuid(information.Kind) == SourceLinkKind)
                    {
                        documents.Add(
                            Encoding.UTF8.GetString(
                                reader.GetBlobBytes(information.Value)));
                    }
                }
            }

            return documents.ToArray();
        }
    }
}
'@
    Add-Type -TypeDefinition $portablePdbInspector
}

function Get-Sha256Hex {
    param(
        [Parameter(Mandatory)]
        [IO.Stream] $Stream
    )

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($Stream))
}

function Get-FileSha256Hex {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $stream = [IO.File]::OpenRead($Path)
    try {
        return Get-Sha256Hex -Stream $stream
    }
    finally {
        $stream.Dispose()
    }
}

function Get-SingleXmlElement {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlElement] $Parent,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $PackageName
    )

    $elements = @($Parent.GetElementsByTagName($Name))
    if ($elements.Count -ne 1) {
        throw (
            "Package $PackageName contains $($elements.Count) " +
            "'$Name' metadata elements; expected exactly one.")
    }

    return $elements[0]
}

function Read-NuspecMetadata {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchive] $Archive,

        [Parameter(Mandatory)]
        [string] $PackageName
    )

    $nuspecEntries = @(
        $Archive.Entries |
            Where-Object {
                $_.FullName.EndsWith(
                    '.nuspec',
                    [StringComparison]::OrdinalIgnoreCase)
            }
    )
    if ($nuspecEntries.Count -ne 1) {
        throw (
            "Package $PackageName contains " +
            "$($nuspecEntries.Count) nuspec files.")
    }

    $reader = [IO.StreamReader]::new($nuspecEntries[0].Open())
    try {
        [xml] $nuspec = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    $metadata = @($nuspec.GetElementsByTagName('metadata'))
    if ($metadata.Count -ne 1) {
        throw "Package $PackageName has invalid nuspec metadata."
    }

    $idElement = Get-SingleXmlElement `
        -Parent $metadata[0] `
        -Name 'id' `
        -PackageName $PackageName
    $versionElement = Get-SingleXmlElement `
        -Parent $metadata[0] `
        -Name 'version' `
        -PackageName $PackageName
    $repositoryElement = Get-SingleXmlElement `
        -Parent $metadata[0] `
        -Name 'repository' `
        -PackageName $PackageName

    return [pscustomobject] @{
        Id = [string] $idElement.InnerText
        Version = [string] $versionElement.InnerText
        Metadata = $metadata[0]
        RepositoryUrl = [string] $repositoryElement.GetAttribute('url')
        RepositoryType = [string] $repositoryElement.GetAttribute('type')
    }
}

function Assert-RepositoryMetadata {
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $Metadata,

        [Parameter(Mandatory)]
        [string] $PackageName
    )

    if ($Metadata.RepositoryUrl -cne $repositoryUrl) {
        throw (
            "Package $PackageName has repository URL " +
            "'$($Metadata.RepositoryUrl)'; expected '$repositoryUrl'.")
    }
    if ($Metadata.RepositoryType -cne 'git') {
        throw (
            "Package $PackageName has repository type " +
            "'$($Metadata.RepositoryType)'; expected 'git'.")
    }
}

function Assert-ArchiveContent {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchive] $Archive,

        [Parameter(Mandatory)]
        [string] $EntryName,

        [Parameter(Mandatory)]
        [string] $ExpectedHash,

        [Parameter(Mandatory)]
        [string] $PackageName
    )

    $entries = @(
        $Archive.Entries |
            Where-Object {
                [string]::Equals(
                    $_.FullName,
                    $EntryName,
                    [StringComparison]::OrdinalIgnoreCase)
            }
    )
    if ($entries.Count -ne 1) {
        throw (
            "Package $PackageName contains $($entries.Count) " +
            "'$EntryName' entries; expected exactly one.")
    }
    if ($entries[0].FullName -cne $EntryName) {
        throw (
            "Package $PackageName contains '$($entries[0].FullName)'; " +
            "expected the exact entry name '$EntryName'.")
    }

    $stream = $entries[0].Open()
    try {
        $actualHash = Get-Sha256Hex -Stream $stream
    }
    finally {
        $stream.Dispose()
    }
    if ($actualHash -cne $ExpectedHash) {
        throw (
            "Package $PackageName contains modified '$EntryName' content.")
    }
}

function Assert-SourceLinkDocument {
    param(
        [Parameter(Mandatory)]
        [string] $Json,

        [Parameter(Mandatory)]
        [string] $PackageName,

        [Parameter(Mandatory)]
        [string] $PdbEntryName
    )

    try {
        $sourceLink = ConvertFrom-Json `
            -InputObject $Json `
            -AsHashtable `
            -Depth 16
    }
    catch {
        throw (
            "Package $PackageName PDB '$PdbEntryName' contains invalid " +
            "SourceLink JSON: $($_.Exception.Message)")
    }

    if ($sourceLink -isnot [Collections.IDictionary] -or
        -not $sourceLink.Contains('documents') -or
        $sourceLink['documents'] -isnot [Collections.IDictionary]) {
        throw (
            "Package $PackageName PDB '$PdbEntryName' has no SourceLink " +
            "'documents' object.")
    }

    $verifiedMappings = 0
    foreach ($mapping in $sourceLink['documents'].GetEnumerator()) {
        $sourcePattern = [string] $mapping.Key
        $targetPattern = $mapping.Value
        if ([string]::IsNullOrWhiteSpace($sourcePattern) -or
            -not $sourcePattern.EndsWith(
                '*',
                [StringComparison]::Ordinal) -or
            $targetPattern -isnot [string] -or
            -not [Text.RegularExpressions.Regex]::IsMatch(
                $targetPattern,
                $sourceLinkUrlPattern,
                [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
            throw (
                "Package $PackageName PDB '$PdbEntryName' contains an " +
                "unverifiable SourceLink mapping '$sourcePattern' -> " +
                "'$targetPattern'.")
        }

        $verifiedMappings++
    }

    if ($verifiedMappings -eq 0) {
        throw (
            "Package $PackageName PDB '$PdbEntryName' contains an empty " +
            "SourceLink documents map.")
    }

    return $verifiedMappings
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

$expectedSymbolVersions =
    [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
foreach ($runtimeIdentifier in $runtimeIdentifiers) {
    $expectedSymbolVersions.Add(
        "$toolPackageId.$runtimeIdentifier",
        $ToolVersion)
}
$expectedSymbolVersions.Add($sdkPackageId, $SdkVersion)

$licenseHash = Get-FileSha256Hex -Path $licensePath
$readmeHash = Get-FileSha256Hex -Path $readmePath
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
        $metadata = Read-NuspecMetadata `
            -Archive $archive `
            -PackageName $package.Name
        Assert-RepositoryMetadata `
            -Metadata $metadata `
            -PackageName $package.Name

        $licenseElement = Get-SingleXmlElement `
            -Parent $metadata.Metadata `
            -Name 'license' `
            -PackageName $package.Name
        if ([string] $licenseElement.GetAttribute('type') -cne 'expression' -or
            [string] $licenseElement.InnerText -cne 'MIT') {
            throw (
                "Package $($package.Name) must declare the exact MIT " +
                "license expression.")
        }

        $readmeElement = Get-SingleXmlElement `
            -Parent $metadata.Metadata `
            -Name 'readme' `
            -PackageName $package.Name
        if ([string] $readmeElement.InnerText -cne 'README.md') {
            throw (
                "Package $($package.Name) declares readme " +
                "'$($readmeElement.InnerText)'; expected 'README.md'.")
        }

        Assert-ArchiveContent `
            -Archive $archive `
            -EntryName 'LICENSE' `
            -ExpectedHash $licenseHash `
            -PackageName $package.Name
        Assert-ArchiveContent `
            -Archive $archive `
            -EntryName 'README.md' `
            -ExpectedHash $readmeHash `
            -PackageName $package.Name
    }
    finally {
        $archive.Dispose()
    }

    $id = $metadata.Id
    $version = $metadata.Version
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

$symbolPackages = @(
    Get-ChildItem -File -LiteralPath $packageDirectoryPath |
        Where-Object Extension -CEQ '.snupkg'
)
if ($symbolPackages.Count -ne $expectedSymbolVersions.Count) {
    throw (
        "Expected $($expectedSymbolVersions.Count) symbol packages in " +
        "$packageDirectoryPath; found $($symbolPackages.Count). The outer " +
        "RID selector is the only package without implementation symbols.")
}

$actualSymbolPackageIds = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($symbolPackage in $symbolPackages) {
    $archive = [IO.Compression.ZipFile]::OpenRead($symbolPackage.FullName)
    try {
        $metadata = Read-NuspecMetadata `
            -Archive $archive `
            -PackageName $symbolPackage.Name
        Assert-RepositoryMetadata `
            -Metadata $metadata `
            -PackageName $symbolPackage.Name

        $id = $metadata.Id
        $version = $metadata.Version
        if (-not $expectedSymbolVersions.ContainsKey($id)) {
            throw (
                "Unexpected symbol package '$id' in " +
                "$($symbolPackage.Name).")
        }
        if (-not $actualSymbolPackageIds.Add($id)) {
            throw "Duplicate symbol package '$id' in $packageDirectoryPath."
        }

        $expectedVersion = $expectedSymbolVersions[$id]
        if ($version -cne $expectedVersion) {
            throw (
                "Symbol package '$id' has version '$version'; expected " +
                "'$expectedVersion'.")
        }
        $expectedFileName = "$id.$expectedVersion.snupkg"
        if ($symbolPackage.Name -cne $expectedFileName) {
            throw (
                "Symbol package '$id' has file name " +
                "'$($symbolPackage.Name)'; expected '$expectedFileName'.")
        }

        $pdbEntries = @(
            $archive.Entries |
                Where-Object {
                    $_.FullName.EndsWith(
                        '.pdb',
                        [StringComparison]::OrdinalIgnoreCase)
                }
        )
        if ($pdbEntries.Count -eq 0) {
            throw (
                "Symbol package $($symbolPackage.Name) contains no " +
                "portable PDB.")
        }

        $pdbEntryNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        $verifiedSourceLinkMappings = 0
        foreach ($pdbEntry in $pdbEntries) {
            if (-not $pdbEntryNames.Add($pdbEntry.FullName)) {
                throw (
                    "Symbol package $($symbolPackage.Name) contains " +
                    "duplicate PDB entry '$($pdbEntry.FullName)'.")
            }
            if ($pdbEntry.Length -eq 0) {
                throw (
                    "Symbol package $($symbolPackage.Name) contains empty " +
                    "PDB '$($pdbEntry.FullName)'.")
            }

            $stream = $pdbEntry.Open()
            try {
                try {
                    $sourceLinkDocuments =
                        [ReleasePackagePortablePdbInspector]::
                            ReadSourceLinkDocuments($stream)
                }
                catch {
                    throw (
                        "Symbol package $($symbolPackage.Name) PDB " +
                        "'$($pdbEntry.FullName)' is not a valid portable " +
                        "PDB: $($_.Exception.Message)")
                }
            }
            finally {
                $stream.Dispose()
            }

            foreach ($sourceLinkDocument in $sourceLinkDocuments) {
                $verifiedSourceLinkMappings +=
                    Assert-SourceLinkDocument `
                        -Json $sourceLinkDocument `
                        -PackageName $symbolPackage.Name `
                        -PdbEntryName $pdbEntry.FullName
            }
        }

        if ($verifiedSourceLinkMappings -eq 0) {
            throw (
                "Symbol package $($symbolPackage.Name) contains no " +
                "verifiable SourceLink mapping.")
        }
    }
    finally {
        $archive.Dispose()
    }
}

$missingSymbolPackageIds = @(
    $expectedSymbolVersions.Keys |
        Where-Object { -not $actualSymbolPackageIds.Contains($_) }
)
if ($missingSymbolPackageIds.Count -gt 0) {
    throw (
        "Missing symbol packages: " +
        "$($missingSymbolPackageIds -join ', ').")
}

Write-Host (
    "Verified tool $ToolVersion for $($runtimeIdentifiers.Count) runtimes " +
    "and SDK $SdkVersion across $($packages.Count) release packages and " +
    "$($symbolPackages.Count) portable-PDB symbol packages.")
