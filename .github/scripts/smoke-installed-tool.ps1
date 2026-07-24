[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,

    [switch] $RunMcpIntegration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageDirectoryPath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packagePrefix = 'DevBitsLab.Mcp.SourceGraph.Tool.'
$outerPackages = @(
    Get-ChildItem -File -LiteralPath $packageDirectoryPath |
        Where-Object {
            $_.Extension -eq '.nupkg' -and
            $_.BaseName.StartsWith(
                $packagePrefix,
                [StringComparison]::Ordinal) -and
            $_.BaseName.Substring($packagePrefix.Length) -match
                '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$'
        }
)
if ($outerPackages.Count -ne 1) {
    throw "Expected one outer tool package in $packageDirectoryPath; found $($outerPackages.Count)."
}
$version = $outerPackages[0].BaseName.Substring($packagePrefix.Length)

$tempBase = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [IO.Path]::GetTempPath()
}
else {
    $env:RUNNER_TEMP
}
$runRoot = Join-Path $tempBase (
    'sourcegraph-mcp-package-smoke-' + [Guid]::NewGuid().ToString('N'))
$toolDirectory = Join-Path $runRoot 'tool'
$nugetConfigPath = Join-Path $runRoot 'NuGet.config'
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

[xml] $nugetConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-pack-output" value="" />
  </packageSources>
</configuration>
'@
$nugetConfig.configuration.packageSources.add.SetAttribute(
    'value',
    $packageDirectoryPath)
$nugetConfig.Save($nugetConfigPath)

& dotnet tool install DevBitsLab.Mcp.SourceGraph.Tool `
    --tool-path $toolDirectory `
    --version $version `
    --configfile $nugetConfigPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool install failed with exit code $LASTEXITCODE."
}

$appHostName = if ($IsWindows) {
    'sourcegraph-mcp.exe'
}
else {
    'sourcegraph-mcp'
}
$appHosts = @(
    Get-ChildItem -Recurse -File -LiteralPath (
        Join-Path $toolDirectory '.store') |
        Where-Object Name -CEQ $appHostName
)
if ($appHosts.Count -ne 1) {
    throw "Expected one installed $appHostName payload; found $($appHosts.Count)."
}
$appHost = $appHosts[0]
$payloadDirectory = $appHost.Directory.FullName

$help = (& $appHost.FullName --help 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Installed tool --help failed with exit code $LASTEXITCODE.`n$help"
}
if (-not $help.Contains(
        'sourcegraph-mcp',
        [StringComparison]::Ordinal)) {
    throw 'Installed tool help did not contain its command name.'
}
if (-not $help.Contains('codex', [StringComparison]::Ordinal)) {
    throw 'Installed tool help did not advertise Codex onboarding.'
}

$chineseHelp = (& $appHost.FullName --help --lang zh 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Installed tool Chinese --help failed with exit code $LASTEXITCODE.`n$chineseHelp"
}
if (-not $chineseHelp.Contains('用法:', [StringComparison]::Ordinal)) {
    throw 'Installed tool Chinese help did not contain its localized usage heading.'
}
if (-not $chineseHelp.Contains('codex', [StringComparison]::Ordinal)) {
    throw 'Installed tool Chinese help did not advertise Codex onboarding.'
}

$codexFixtureRoot = Join-Path $runRoot 'codex-fixture'
[IO.Directory]::CreateDirectory($codexFixtureRoot) | Out-Null
$codexSolutionPath = Join-Path $codexFixtureRoot 'Fixture.slnx'
$codexPreview = (& $appHost.FullName init `
    --yes `
    --client codex `
    --print-only `
    --root $codexFixtureRoot `
    --solution $codexSolutionPath 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Installed tool Codex init preview failed with exit code $LASTEXITCODE.`n$codexPreview"
}
$codexFixtureRootToml = $codexFixtureRoot.Replace('\', '\\')
$codexSolutionPathToml = $codexSolutionPath.Replace('\', '\\')
foreach ($expectedCodexText in @(
        '.codex',
        'config.toml',
        '[mcp_servers.sourcegraph]',
        'command = "sourcegraph-mcp"',
        "`"--solution`", `"$codexSolutionPathToml`"",
        "`"--root`", `"$codexFixtureRootToml`"",
        '"--codex-compat"',
        "cwd = `"$codexFixtureRootToml`"")) {
    if (-not $codexPreview.Contains(
            $expectedCodexText,
            [StringComparison]::Ordinal)) {
        throw (
            "Installed tool Codex init preview did not contain " +
            "'$expectedCodexText'.`n$codexPreview")
    }
}
if ($codexPreview.Contains('${workspaceFolder}', [StringComparison]::Ordinal)) {
    throw "Installed tool Codex init preview leaked the unsupported workspace placeholder.`n$codexPreview"
}

$protocName = if ($IsWindows) {
    'protoc.exe'
}
else {
    'protoc'
}
$protocFiles = @(
    Get-ChildItem -Recurse -File -LiteralPath $payloadDirectory |
        Where-Object Name -CEQ $protocName
)
if ($protocFiles.Count -ne 1) {
    throw "Expected one bundled $protocName; found $($protocFiles.Count)."
}
$protocOutput = (& $protocFiles[0].FullName --version 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Bundled protoc failed with exit code $LASTEXITCODE.`n$protocOutput"
}
if (-not $protocOutput.Contains(
        'libprotoc',
        [StringComparison]::Ordinal)) {
    throw "Bundled protoc returned an unexpected version string: $protocOutput"
}

$nativeNames = if ($IsWindows) {
    @('libclang.dll', 'libClangSharp.dll')
}
elseif ($IsLinux) {
    @('libclang.so', 'libClangSharp.so')
}
elseif ($IsMacOS) {
    @('libclang.dylib', 'libClangSharp.dylib')
}
else {
    throw 'Unsupported package-smoke operating system.'
}
foreach ($nativeName in $nativeNames) {
    $matches = @(
        Get-ChildItem -File -LiteralPath $payloadDirectory |
            Where-Object Name -CEQ $nativeName
    )
    if ($matches.Count -ne 1 -or $matches[0].Length -le 0) {
        throw "Expected one non-empty $nativeName beside the installed app host."
    }
}

$processArchitecture =
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
if ($IsWindows -and $processArchitecture -eq
    [Runtime.InteropServices.Architecture]::X64) {
    $runtimeIdentifier = 'win-x64'
    $architecture = 'x64'
    $compilerAbi = 'msvc'
    $compilerArguments = @(
        '-x',
        'c++',
        '-std=c++17',
        '--target=x86_64-pc-windows-msvc',
        '-fms-extensions',
        '-D_WIN32=1')
}
elseif ($IsLinux -and $processArchitecture -eq
    [Runtime.InteropServices.Architecture]::X64) {
    $runtimeIdentifier = 'linux-x64'
    $architecture = 'x64'
    $compilerAbi = 'itanium'
    $compilerArguments = @(
        '-x',
        'c++',
        '-std=c++17',
        '--target=x86_64-unknown-linux-gnu')
}
elseif ($IsMacOS -and $processArchitecture -eq
    [Runtime.InteropServices.Architecture]::Arm64) {
    $runtimeIdentifier = 'osx-arm64'
    $architecture = 'arm64'
    $compilerAbi = 'itanium'
    $compilerArguments = @(
        '-x',
        'c++',
        '-std=c++17',
        '--target=arm64-apple-darwin')
}
else {
    throw "The installed package smoke does not support $processArchitecture on this OS."
}

$fixtureRoot = Join-Path $runRoot 'native-fixture'
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$sourcePath = Join-Path $fixtureRoot 'runtime_probe.cpp'
[IO.File]::WriteAllText(
    $sourcePath,
    'extern "C" int medinterop_runtime_probe(int value) { return value + 1; }')

$request = [ordered] @{
    version = 2
    kind = 'native-extraction-request'
    request = [ordered] @{
        source_file_path = $sourcePath
        scope_root = $fixtureRoot
        producing_file_id = 1
        target = [ordered] @{
            runtime_identifier = $runtimeIdentifier
            architecture = $architecture
            compiler_abi = $compilerAbi
            pointer_size_bytes = 8
            default_pack = 8
        }
        compiler_arguments = $compilerArguments
        library_name = 'medinterop-runtime-probe'
        exclude_patterns = @()
    }
}
$requestJson = $request | ConvertTo-Json -Depth 8 -Compress
$requestBytes = [Text.Encoding]::UTF8.GetBytes($requestJson)
$networkLength = [Net.IPAddress]::HostToNetworkOrder(
    [int] $requestBytes.Length)
$frameHeader = [BitConverter]::GetBytes($networkLength)

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $appHost.FullName
$startInfo.ArgumentList.Add('--native-worker-v1')
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$process = [Diagnostics.Process]::new()
$process.StartInfo = $startInfo
if (-not $process.Start()) {
    throw 'Failed to start the installed native worker.'
}

$responseStream = [IO.MemoryStream]::new()
$stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($responseStream)
$stderrRead = $process.StandardError.ReadToEndAsync()
try {
    $process.StandardInput.BaseStream.Write(
        $frameHeader,
        0,
        $frameHeader.Length)
    $process.StandardInput.BaseStream.Write(
        $requestBytes,
        0,
        $requestBytes.Length)
    $process.StandardInput.BaseStream.Flush()
    $process.StandardInput.Close()

    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        throw 'Installed native worker timed out.'
    }
    $stdoutCopy.GetAwaiter().GetResult() | Out-Null
    $standardError = $stderrRead.GetAwaiter().GetResult()
    $workerExitCode = $process.ExitCode
}
finally {
    $process.Dispose()
}

$responseBytes = $responseStream.ToArray()
$responseStream.Dispose()
if ($responseBytes.Length -lt 4) {
    throw "Native worker returned no framed response. stderr: $standardError"
}
$responseLength = [Net.IPAddress]::NetworkToHostOrder(
    [BitConverter]::ToInt32($responseBytes, 0))
if ($responseLength -ne $responseBytes.Length - 4) {
    throw "Native worker returned an invalid frame length. stderr: $standardError"
}
$responseJson = [Text.Encoding]::UTF8.GetString(
    $responseBytes,
    4,
    $responseLength)
$response = $responseJson | ConvertFrom-Json
if ($workerExitCode -ne 0 -or -not $response.success) {
    throw (
        "Installed native worker failed with exit code ${workerExitCode}: " +
        "$($response.failure.code) $($response.failure.message). " +
        "stderr: $standardError")
}
if (@($response.result.functions).Count -lt 1) {
    throw 'Installed native worker loaded but did not extract the probe function.'
}

if ($RunMcpIntegration) {
    $priorCommand = $env:SOURCEGRAPH_MCP_INTEGRATION_COMMAND
    try {
        $env:SOURCEGRAPH_MCP_INTEGRATION_COMMAND = $appHost.FullName
        & dotnet test `
            tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests `
            -c Release `
            --no-build `
            --no-restore `
            --filter (
                'FullyQualifiedName=' +
                'DevBitsLab.Mcp.SourceGraph.IntegrationTests.InitializeTests.' +
                'Initialize_and_tools_list_against_Sample_complete_and_exit_cleanly')
        if ($LASTEXITCODE -ne 0) {
            throw "Installed MCP integration smoke failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        $env:SOURCEGRAPH_MCP_INTEGRATION_COMMAND = $priorCommand
    }
}

Write-Host (
    "Installed tool $version passed help, Codex init, protoc, native worker" +
    $(if ($RunMcpIntegration) { ', and MCP' } else { '' }) +
    " smoke checks for $runtimeIdentifier.")
