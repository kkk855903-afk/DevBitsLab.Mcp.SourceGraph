using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class VcxProjectImporterTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "sg-vcx-import-" + Guid.NewGuid().ToString("N"));

    public VcxProjectImporterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Import_selects_configuration_source_and_project_compile_arguments()
    {
        var projectDirectory = Path.Join(_root, "AlgorithmBridge");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Join(projectDirectory, "AlgorithmBridgeHeaderCheck.c"),
            "#include \"AlgorithmBridge.h\"\n");
        File.WriteAllText(
            Path.Join(projectDirectory, "AlgorithmBridge.h"),
            "unsigned int AB_GetAPIVersion(void);\n");
        File.WriteAllText(
            Path.Join(projectDirectory, "Ignored.cpp"),
            "void ignored() {}\n");
        File.WriteAllText(
            Path.Join(projectDirectory, "AlgorithmBridge.vcxproj"),
            ProjectXml);

        var configuration = Configuration(
            "AlgorithmBridge/AlgorithmBridge.vcxproj",
            ["AlgorithmBridgeHeaderCheck.c"]);
        var result = VcxProjectImporter.Import(
            _root,
            configuration,
            new ScopePathPolicy(_root, []));

        result.IsComplete.Should().BeTrue();
        var unit = result.TranslationUnits.Should().ContainSingle().Subject;
        unit.Path.Should().Be(
            "AlgorithmBridge/AlgorithmBridgeHeaderCheck.c");
        unit.Library.Should().Be("AlgorithmBridge.dll");
        unit.Arguments.Should().ContainInOrder(
            "-x",
            "c",
            "--target=x86_64-pc-windows-msvc");
        unit.Arguments.Should().Contain("-DALGORITHM_BRIDGE_EXPORTS");
        unit.Arguments.Should().Contain("-DNDEBUG");
        unit.Arguments.Should().Contain("-DAB_SOURCEGRAPH=1");
        unit.Arguments.Should().Contain(argument =>
            argument.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                .EndsWith(
                "AlgorithmBridge",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Import_rejects_configuration_platform_mismatch()
    {
        var projectDirectory = Path.Join(_root, "AlgorithmBridge");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Join(projectDirectory, "AlgorithmBridgeHeaderCheck.c"),
            "unsigned int AB_GetAPIVersion(void);\n");
        File.WriteAllText(
            Path.Join(projectDirectory, "AlgorithmBridge.vcxproj"),
            ProjectXml);
        var configuration = Configuration(
            "AlgorithmBridge/AlgorithmBridge.vcxproj",
            ["AlgorithmBridgeHeaderCheck.c"]) with
        {
            VcxProjects =
            [
                new InteropVcxProjectConfig(
                    "AlgorithmBridge/AlgorithmBridge.vcxproj",
                    "Release",
                    "Win32",
                    "AlgorithmBridge.dll",
                    ["AlgorithmBridgeHeaderCheck.c"],
                    [],
                    null),
            ],
        };

        var result = VcxProjectImporter.Import(
            _root,
            configuration,
            new ScopePathPolicy(_root, []));

        result.IsComplete.Should().BeFalse();
        result.Failures.Should().ContainSingle(failure =>
            failure.Code == "vcxproj-configuration-not-found"
            || failure.Code == "vcxproj-target-mismatch");
    }

    [SkippableFact]
    public void Br01_AlgorithmBridge_vcxproj_reaches_clang_export_and_pinvoke_match()
    {
        var brRoot = Environment.GetEnvironmentVariable(
            "SOURCEGRAPH_BR01_ROOT");
        Skip.If(
            string.IsNullOrWhiteSpace(brRoot)
            || !File.Exists(Path.Join(
                brRoot,
                "AlgorithmBridge",
                "AlgorithmBridge.vcxproj")),
            "Set SOURCEGRAPH_BR01_ROOT to run the BR-01 compatibility probe.");

        var configuration = Configuration(
            "AlgorithmBridge/AlgorithmBridge.vcxproj",
            ["AlgorithmBridgeHeaderCheck.c"]);
        var imported = VcxProjectImporter.Import(
            brRoot!,
            configuration,
            new ScopePathPolicy(brRoot!, []));

        imported.IsComplete.Should().BeTrue(
            string.Join(
                " | ",
                imported.Failures.Select(failure => failure.Message)));
        var unit = imported.TranslationUnits.Should().ContainSingle().Subject;
        unit.SystemIncludeDirectories.Should().Contain(directory =>
            File.Exists(Path.Join(directory, "stdint.h")));
        var extraction = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                Path.Join(brRoot!, unit.Path),
                brRoot!,
                ProducingFileId: 1,
                configuration.Target,
                unit.Arguments,
                unit.Library)
            {
                SystemIncludeDirectories =
                    unit.SystemIncludeDirectories,
            });
        Skip.If(
            extraction.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "CLANG0001"),
            "The current test runtime does not carry a compatible libclang native asset.");

        extraction.HasErrors.Should().BeFalse(
            string.Join(
                " | ",
                extraction.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Location?.FilePath}:"
                    + $"{diagnostic.Location?.StartLine}: {diagnostic.Message}")));
        var export = extraction.Exports.Should()
            .ContainSingle(candidate =>
                candidate.ExportName == "AB_GetAPIVersion")
            .Subject;

        var uint32 = new AbiTypeRef(
            "uint32",
            AbiTypeCategory.UnsignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: false);
        var managed = new ManagedImport(
            "csharp:M:AlgorithmBridgeNative.AB_GetAPIVersion",
            ManagedImportKind.DllImport,
            "AlgorithmBridge.dll",
            "AB_GetAPIVersion",
            InteropCallingConvention.Cdecl,
            uint32,
            [],
            CharacterSet: null,
            SetLastError: false,
            configuration.Target,
            new Evidence(
                2,
                new SourceLocation(
                    "BioDetector.AlgorithmService/Interop/AlgorithmBridgeNative.cs",
                    51,
                    1,
                    51,
                    80),
                EvidenceConfidence.Exact,
                "managed-interop"))
        {
            ExactSpelling = true,
        };

        var match = new InteropMatcher().Match(managed, [export]);

        match.Status.Should().Be(InteropMatchStatus.SourceMatched);
        match.NativeSymbolCanonicalKey.Should().Be(
            export.SymbolCanonicalKey);

        var managedSourcePath = Path.Join(
            brRoot!,
            "BioDetector.AlgorithmService",
            "Interop",
            "AlgorithmBridgeNative.cs");
        File.Exists(managedSourcePath).Should().BeTrue();
        var managedSource = File.ReadAllText(managedSourcePath);
        managedSource.Should().Contain("DllImport");
        managedSource.Should().Contain("AB_GetAPIVersion");
    }

    [SkippableFact]
    public void Br01_remaining_native_projects_import_release_x64_compile_items()
    {
        var brRoot = Environment.GetEnvironmentVariable(
            "SOURCEGRAPH_BR01_ROOT");
        Skip.If(
            string.IsNullOrWhiteSpace(brRoot),
            "Set SOURCEGRAPH_BR01_ROOT to run the BR-01 compatibility probe.");

        var projects = new[]
        {
            "BioBufferAccess/BioBufferAccess.vcxproj",
            "BioCommonLib/BioCommonLib.vcxproj",
            "BioController/BioController.vcxproj",
            "BioDeepLearningCall/BioDeepLearningCall.vcxproj",
            "BioIrisDetector/BioIrisDetector.vcxproj",
            "BioMainControl/BioMainControl.vcxproj",
            "BioOctScanner/BioOctScanner.vcxproj",
            "OseData/InfoExData.vcxproj",
        };
        var failures = new List<string>();
        foreach (var projectPath in projects)
        {
            if (!File.Exists(Path.Join(brRoot!, projectPath)))
            {
                failures.Add($"{projectPath}: missing");
                continue;
            }
            var configuration = Configuration(projectPath, []) with
            {
                VcxProjects =
                [
                    new InteropVcxProjectConfig(
                        projectPath,
                        "Release",
                        "x64",
                        Path.GetFileNameWithoutExtension(projectPath) + ".dll",
                        [],
                        [],
                        null),
                ],
            };
            var imported = VcxProjectImporter.Import(
                brRoot!,
                configuration,
                new ScopePathPolicy(brRoot!, []));
            if (!imported.IsComplete)
            {
                failures.AddRange(imported.Failures.Select(failure =>
                    $"{projectPath}: {failure.Code}: {failure.Message}"));
            }
            else if (imported.TranslationUnits.Count == 0)
            {
                failures.Add($"{projectPath}: no translation units");
            }
        }

        failures.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Br01_solution_autoConfiguration_selectsNineProjectsAndAllCompileItems()
    {
        var brRoot = Environment.GetEnvironmentVariable(
            "SOURCEGRAPH_BR01_ROOT");
        const string solutionName = "BioDetectorV2_zeiss.sln";
        Skip.If(
            string.IsNullOrWhiteSpace(brRoot)
            || !File.Exists(Path.Join(brRoot, solutionName)),
            "Set SOURCEGRAPH_BR01_ROOT to run the BR-01 compatibility probe.");
        var scope = new Scope(
            "br01",
            "BR-01",
            brRoot!,
            new ScopeProjectSet.Solutions([solutionName], []),
            Isolated: false,
            DateTimeOffset.MinValue);

        var resolution = SolutionNativeInteropResolver.Resolve(scope);

        resolution.DiscoveredProjects.Should().Be(9);
        resolution.Configuration.Should().NotBeNull();
        resolution.Configuration!.VcxProjects.Should().HaveCount(9);
        resolution.Configuration.VcxProjects.Should().OnlyContain(project =>
            project.SourceFiles.Count == 0);
        var imported = VcxProjectImporter.Import(
            brRoot!,
            resolution.Configuration,
            new ScopePathPolicy(brRoot!, []));

        imported.AttemptedProjects.Should().Be(9);
        imported.ImportedProjects.Should().HaveCount(9);
        imported.TranslationUnits.Should().HaveCount(66);
        imported.TranslationUnits.Should().Contain(unit =>
            unit.Path.Replace('\\', '/')
                .EndsWith(
                    "AlgorithmBridge/AlgorithmBridge.cpp",
                    StringComparison.OrdinalIgnoreCase));
        var algorithmBridge = imported.TranslationUnits.Single(unit =>
            unit.Path.Replace('\\', '/')
                .EndsWith(
                    "AlgorithmBridge/AlgorithmBridge.cpp",
                    StringComparison.OrdinalIgnoreCase));
        var extractionRequest = new ClangNativeExtractionRequest(
                Path.Join(brRoot!, algorithmBridge.Path),
                brRoot!,
                ProducingFileId: 1,
                resolution.Configuration.Target,
                algorithmBridge.Arguments,
                algorithmBridge.Library)
            {
                SystemIncludeDirectories =
                    algorithmBridge.SystemIncludeDirectories,
            };
        var protectedInputs =
            await ProtectedNativeInputPreparer.PrepareAsync(
                brRoot!,
                extractionRequest,
                CancellationToken.None);
        protectedInputs.IsSuccess.Should().BeTrue(
            protectedInputs.FailureMessage);
        var extraction = ClangNativeExtractor.Extract(
            protectedInputs.Request);
        Skip.If(
            extraction.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "CLANG0001"),
            "The current test runtime does not carry a compatible libclang native asset.");
        extraction.HasErrors.Should().BeFalse(
            string.Join(
                " | ",
                extraction.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Location?.FilePath}:"
                    + $"{diagnostic.Location?.StartLine}: {diagnostic.Message}")));
        var workerResponse = new NativeWorkerResponseEnvelope(
            NativeWorkerProtocol.CurrentVersion,
            NativeWorkerProtocol.ResponseKind,
            Success: true,
            extraction,
            Failure: null,
            NativeWorkerIsolationCapabilities.Baseline);
        var encodedWorkerResponse = NativeWorkerProtocol.EncodeWorkerResponse(
            workerResponse,
            protectedInputs.Request);
        NativeWorkerProtocol.DecodeResponse(
                encodedWorkerResponse,
                protectedInputs.Request)
            .Result.Should().BeEquivalentTo(extraction);
        var export = extraction.Exports.Should().ContainSingle(candidate =>
            candidate.ExportName == "AB_GetAPIVersion"
            && candidate.Evidence.Location.StartLine == 84,
            string.Join(
                " | ",
                extraction.Exports.Select(candidate =>
                    $"export:{candidate.ExportName}@{candidate.Evidence.Location.StartLine}")
                .Concat(extraction.Functions
                    .Where(candidate =>
                        candidate.Name.Contains(
                            "AB_GetAPIVersion",
                            StringComparison.Ordinal))
                    .Select(candidate =>
                        $"function:{candidate.Name}@{candidate.Evidence.Location.StartLine}:exported={candidate.IsExported}:definition={candidate.IsDefinition}:usr={candidate.DeclarationUsr}"))))
            .Subject;
        extraction.Calls.Should().Contain(call =>
            call.CallerSymbolCanonicalKey.Contains(
                "AB_InitializeRefraction",
                StringComparison.Ordinal)
            && ((!string.IsNullOrEmpty(call.CalleeSymbolCanonicalKey)
                    && call.CalleeSymbolCanonicalKey.Contains(
                        "AB_ShutdownRefraction",
                        StringComparison.Ordinal))
                || call.ReferencedDeclarationUsr.Contains(
                    "AB_ShutdownRefraction",
                    StringComparison.Ordinal)));
        var managed = new ManagedImport(
            "csharp:M:AlgorithmBridgeNative.AB_GetAPIVersion",
            ManagedImportKind.DllImport,
            "AlgorithmBridge.dll",
            "AB_GetAPIVersion",
            export.CallingConvention,
            export.ReturnType,
            [],
            CharacterSet: null,
            SetLastError: false,
            resolution.Configuration.Target,
            new Evidence(
                2,
                new SourceLocation(
                    "BioDetector.AlgorithmService/Interop/AlgorithmBridgeNative.cs",
                    51,
                    1,
                    51,
                    80),
                EvidenceConfidence.Exact,
                "managed-interop"))
        {
            ExactSpelling = true,
        };
        new InteropMatcher()
            .Match(
                managed,
                [export],
                isExportUniverseComplete: false)
            .Status.Should().Be(InteropMatchStatus.SourceMatched);
    }

    [SkippableFact]
    public async Task Br01_failed_managed_project_retains_pinvoke_through_safe_fallback()
    {
        var brRoot = Environment.GetEnvironmentVariable(
            "SOURCEGRAPH_BR01_ROOT");
        const string solutionName = "BioDetectorV2_zeiss.sln";
        Skip.If(
            string.IsNullOrWhiteSpace(brRoot)
            || !File.Exists(Path.Join(brRoot, solutionName)),
            "Set SOURCEGRAPH_BR01_ROOT to run the BR-01 compatibility probe.");
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "br01-managed-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            await using var store = new SqliteGraphStore(
                Path.Combine(temporaryDirectory, "graph.db"));
            await using var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: brRoot,
                excludePatterns: [],
                interopTarget: InteropTarget.WindowsX64Msvc);

            await indexer.OpenAsync(Path.Join(brRoot!, solutionName));
            var algorithmService = indexer.SanitizedSolution!.Projects
                .Single(project => string.Equals(
                    project.FilePath,
                    Path.Join(
                        brRoot,
                        "BioDetector.AlgorithmService",
                        "BioDetector.AlgorithmService.csproj"),
                    StringComparison.OrdinalIgnoreCase));
            algorithmService.Documents.Should().Contain(document =>
                string.Equals(
                    document.FilePath,
                    Path.Join(
                        brRoot,
                        "BioDetector.AlgorithmService",
                        "Interop",
                        "AlgorithmBridgeNative.cs"),
                    StringComparison.OrdinalIgnoreCase));

            var result = await indexer.IndexAllAsync();
            var imports =
                await InteropFactStoreReader.ReadManagedImportsAsync(store);

            result.FailedProjects.Should().NotBeEmpty();
            imports.Facts.Should().Contain(stored =>
                stored.Fact.EntryPoint == "AB_GetAPIVersion"
                && stored.Fact.Evidence.Location.FilePath.EndsWith(
                    "BioDetector.AlgorithmService"
                    + Path.DirectorySeparatorChar
                    + "Interop"
                    + Path.DirectorySeparatorChar
                    + "AlgorithmBridgeNative.cs",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch
            {
                // Best effort for Windows test-host handles.
            }
        }
    }

    [SkippableFact]
    public async Task Br01_installed_scope_database_contains_exact_positive_boundary_inputs()
    {
        var brRoot = Environment.GetEnvironmentVariable(
            "SOURCEGRAPH_BR01_ROOT");
        Skip.If(
            string.IsNullOrWhiteSpace(brRoot),
            "Set SOURCEGRAPH_BR01_ROOT to run the BR-01 compatibility probe.");
        var sourceDatabase = Path.Join(
            brRoot!,
            ".sourcegraph",
            "scopes",
            "default.db");
        Skip.If(
            !File.Exists(sourceDatabase),
            "Run the installed BR-01 solution smoke test first.");
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "br01-installed-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var copiedDatabase = Path.Combine(
            temporaryDirectory,
            "default.db");
        File.Copy(sourceDatabase, copiedDatabase);
        try
        {
            await using var store = new SqliteGraphStore(copiedDatabase);
            var managed =
                await InteropFactStoreReader.ReadManagedImportsAsync(store);
            var native =
                await InteropFactStoreReader.ReadNativeExportsAsync(store);
            var managedImport = managed.Facts.SingleOrDefault(stored =>
                stored.Fact.EntryPoint == "AB_GetAPIVersion");
            var nativeExport = native.Facts.SingleOrDefault(stored =>
                stored.Fact.ExportName == "AB_GetAPIVersion");

            managedImport.Should().NotBeNull(
                string.Join(
                    " | ",
                    managed.Facts.Select(stored =>
                        $"{stored.Fact.EntryPoint}@{stored.Row.FilePath}")));
            nativeExport.Should().NotBeNull(
                string.Join(
                    " | ",
                    native.Facts.Select(stored =>
                        $"{stored.Fact.ExportName}@"
                        + $"{stored.Fact.Evidence.Location.FilePath}:"
                        + stored.Fact.Evidence.Location.StartLine)));
            var match = new InteropMatcher().Match(
                managedImport!.Fact,
                native.Facts.Select(stored => stored.Fact).ToArray(),
                isExportUniverseComplete: false);
            match.Status.Should().Be(InteropMatchStatus.SourceMatched);
            match.NativeSymbolCanonicalKey.Should().Be(
                nativeExport!.Fact.SymbolCanonicalKey);
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch
            {
                // Best effort for Windows test-host handles.
            }
        }
    }

    [Fact]
    public void Roslyn_workspace_classifies_vcxproj_diagnostics_as_native()
    {
        RoslynIndexer.IsVisualCppWorkspaceDiagnostic(
                "Cannot open project 'native/AlgorithmBridge.vcxproj' because C++ is not supported.")
            .Should().BeTrue();
        RoslynIndexer.IsVisualCppWorkspaceDiagnostic(
                "Cannot open project 'managed/Broken.csproj'.")
            .Should().BeFalse();
    }

    private static ScopeInteropConfig Configuration(
        string projectPath,
        IReadOnlyList<string> sourceFiles) =>
        new(
            InteropTarget.WindowsX64Msvc,
            [])
        {
            VcxProjects =
            [
                new InteropVcxProjectConfig(
                    projectPath,
                    "Release",
                    "x64",
                    "AlgorithmBridge.dll",
                    sourceFiles,
                    ["-DAB_SOURCEGRAPH=1"],
                    null),
            ],
        };

    private const string ProjectXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <ItemGroup Label="ProjectConfigurations">
            <ProjectConfiguration Include="Release|x64">
              <Configuration>Release</Configuration>
              <Platform>x64</Platform>
            </ProjectConfiguration>
          </ItemGroup>
          <PropertyGroup Label="Globals">
            <WindowsTargetPlatformVersion>10.0.20348.0</WindowsTargetPlatformVersion>
          </PropertyGroup>
          <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Release|x64'" Label="Configuration">
            <ConfigurationType>DynamicLibrary</ConfigurationType>
            <PlatformToolset>v141</PlatformToolset>
            <CharacterSet>Unicode</CharacterSet>
          </PropertyGroup>
          <ItemDefinitionGroup Condition="'$(Configuration)|$(Platform)'=='Release|x64'">
            <ClCompile>
              <PreprocessorDefinitions>NDEBUG;ALGORITHM_BRIDGE_EXPORTS;%(PreprocessorDefinitions)</PreprocessorDefinitions>
              <AdditionalIncludeDirectories>$(ProjectDir);%(AdditionalIncludeDirectories)</AdditionalIncludeDirectories>
              <PrecompiledHeader>NotUsing</PrecompiledHeader>
            </ClCompile>
          </ItemDefinitionGroup>
          <ItemGroup>
            <ClCompile Include="AlgorithmBridgeHeaderCheck.c" />
            <ClCompile Include="Ignored.cpp" />
          </ItemGroup>
        </Project>
        """;
}
