using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ScopeConfigInteropTests : IDisposable
{
    private readonly string _tempDir =
        Path.Join(Path.GetTempPath(), "sg-interop-scope-config-" + Guid.NewGuid().ToString("N"));

    public ScopeConfigInteropTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Load_reads_explicit_target_and_ordered_translation_units()
    {
        WriteConfig("""
            {
              "scopes": [
                {
                  "name": "native",
                  "paths": ["src/**/*.csproj"],
                  "interop": {
                    "target": {
                      "runtime_identifier": "win-x64",
                      "architecture": "x64",
                      "compiler_abi": "msvc",
                      "pointer_size_bytes": 8,
                      "default_pack": 8
                    },
                    "translation_units": [
                      {
                        "path": "native/first.cpp",
                        "library": "medalgo",
                        "arguments": ["-std=c++20", "-DMEDALGO_EXPORTS"],
                        "binary_path": "artifacts/medalgo.dll"
                      },
                      {
                        "path": "native/second.cpp",
                        "library": "medalgo",
                        "arguments": ["-std=c++20"]
                      }
                    ]
                  }
                }
              ]
            }
            """);

        var interop = ScopeConfigLoader.Load(_tempDir).Scopes.Single().Interop;

        interop.Should().NotBeNull();
        interop!.Target.RuntimeIdentifier.Should().Be("win-x64");
        interop.Target.Architecture.Should().Be(InteropArchitecture.X64);
        interop.Target.CompilerAbi.Should().Be(InteropCompilerAbi.Msvc);
        interop.Target.PointerSizeBytes.Should().Be(8);
        interop.Target.DefaultPack.Should().Be(8);
        interop.TranslationUnits.Select(unit => unit.Path)
            .Should().ContainInOrder("native/first.cpp", "native/second.cpp");
        interop.TranslationUnits[0].Arguments
            .Should().ContainInOrder("-std=c++20", "-DMEDALGO_EXPORTS");
        interop.TranslationUnits[0].BinaryPath.Should().Be("artifacts/medalgo.dll");
        interop.TranslationUnits[1].BinaryPath.Should().BeNull();
    }

    [Fact]
    public void Save_then_load_round_trips_interop_configuration()
    {
        var original = new ScopeConfig(
            [
                new Scope(
                    Id: "native",
                    Name: "native",
                    Root: _tempDir,
                    ProjectSet: new ScopeProjectSet.Paths(["src/**/*.csproj"], []),
                    Isolated: false,
                    LastIndexedAt: DateTimeOffset.MinValue)
                {
                    Interop = new ScopeInteropConfig(
                        new InteropTarget(
                            "linux-arm64",
                            InteropArchitecture.Arm64,
                            InteropCompilerAbi.Itanium,
                            pointerSizeBytes: 8,
                            defaultPack: 16),
                        [
                            new InteropTranslationUnitConfig(
                                "native/interop.cpp",
                                "libmedalgo.so",
                                ["--target=aarch64-linux-gnu", "-std=c++20"],
                                "build/libmedalgo.so"),
                        ]),
                },
            ],
            DefaultScope: "native");

        ScopeConfigLoader.Save(_tempDir, original);
        var json = File.ReadAllText(Path.Join(_tempDir, ScopeConfigLoader.FileName));
        var roundTripped = ScopeConfigLoader.Load(_tempDir).Scopes.Single().Interop;

        json.Should().Contain("\"translation_units\"");
        json.Should().Contain("\"runtime_identifier\": \"linux-arm64\"");
        roundTripped.Should().NotBeNull();
        roundTripped!.Target.RuntimeIdentifier.Should().Be("linux-arm64");
        roundTripped.Target.Architecture.Should().Be(InteropArchitecture.Arm64);
        roundTripped.Target.CompilerAbi.Should().Be(InteropCompilerAbi.Itanium);
        roundTripped.Target.PointerSizeBytes.Should().Be(8);
        roundTripped.Target.DefaultPack.Should().Be(16);
        roundTripped.TranslationUnits.Should().ContainSingle();
        roundTripped.TranslationUnits[0].Should().BeEquivalentTo(
            original.Scopes[0].Interop!.TranslationUnits[0]);
    }

    [Fact]
    public void Load_and_save_round_trips_static_vcxproj_import()
    {
        WriteConfig("""
            {
              "scopes": [
                {
                  "name": "native",
                  "paths": ["src/**/*.csproj"],
                  "interop": {
                    "target": {
                      "runtime_identifier": "win-x64",
                      "architecture": "x64",
                      "compiler_abi": "msvc",
                      "pointer_size_bytes": 8,
                      "default_pack": 8
                    },
                    "vcx_projects": [
                      {
                        "path": "AlgorithmBridge/AlgorithmBridge.vcxproj",
                        "configuration": "Release",
                        "platform": "x64",
                        "library": "AlgorithmBridge.dll",
                        "source_files": ["AlgorithmBridgeHeaderCheck.c"],
                        "additional_arguments": ["-DAB_SOURCEGRAPH=1"],
                        "binary_path": "Release/AlgorithmBridge.dll"
                      }
                    ]
                  }
                }
              ]
            }
            """);

        var loaded = ScopeConfigLoader.Load(_tempDir);
        var interop = loaded.Scopes.Single().Interop!;

        interop.TranslationUnits.Should().BeEmpty();
        interop.VcxProjects.Should().ContainSingle();
        interop.VcxProjects[0].Should().BeEquivalentTo(
            new InteropVcxProjectConfig(
                "AlgorithmBridge/AlgorithmBridge.vcxproj",
                "Release",
                "x64",
                "AlgorithmBridge.dll",
                ["AlgorithmBridgeHeaderCheck.c"],
                ["-DAB_SOURCEGRAPH=1"],
                "Release/AlgorithmBridge.dll"));

        ScopeConfigLoader.Save(_tempDir, loaded);
        var serialized = File.ReadAllText(
            Path.Join(_tempDir, ScopeConfigLoader.FileName));
        var roundTripped = ScopeConfigLoader.Load(_tempDir)
            .Scopes.Single().Interop!;

        serialized.Should().Contain("\"vcx_projects\"");
        serialized.Should().NotContain("\"translation_units\"");
        roundTripped.VcxProjects[0].Should().BeEquivalentTo(
            interop.VcxProjects[0]);
    }

    [Fact]
    public void Load_rejects_vcxproj_without_explicit_configuration()
    {
        WriteConfig("""
            {
              "scopes": [
                {
                  "name": "native",
                  "paths": ["src/**/*.csproj"],
                  "interop": {
                    "target": {
                      "runtime_identifier": "win-x64",
                      "architecture": "x64",
                      "compiler_abi": "msvc",
                      "pointer_size_bytes": 8,
                      "default_pack": 8
                    },
                    "vcx_projects": [
                      {
                        "path": "AlgorithmBridge/AlgorithmBridge.vcxproj",
                        "platform": "x64",
                        "library": "AlgorithmBridge.dll"
                      }
                    ]
                  }
                }
              ]
            }
            """);

        var act = () => ScopeConfigLoader.Load(_tempDir);

        act.Should().Throw<ScopeConfigException>()
            .WithMessage("*configuration*non-empty*");
    }

    [Fact]
    public void Legacy_scope_without_interop_remains_supported_and_serialises_without_block()
    {
        WriteConfig("""
            {
              "scopes": [
                { "name": "managed", "solutions": ["managed.slnx"] }
              ]
            }
            """);

        var config = ScopeConfigLoader.Load(_tempDir);
        config.Scopes.Single().Interop.Should().BeNull();

        ScopeConfigLoader.Serialise(config).Should().NotContain("\"interop\"");
    }

    [Theory]
    [InlineData("", "x64", "msvc", 8, 8, "interop.target")]
    [InlineData("win-x64", "X64", "msvc", 8, 8, "architecture")]
    [InlineData("win-x64", "x64", "gcc", 8, 8, "compiler_abi")]
    [InlineData("win-x86", "x86", "msvc", 8, 8, "pointer_size_bytes")]
    [InlineData("win-x64", "x64", "msvc", 16, 8, "pointer_size_bytes")]
    [InlineData("win-x64", "x64", "msvc", 8, 3, "Default pack")]
    public void Load_rejects_invalid_target_values(
        string runtimeIdentifier,
        string architecture,
        string compilerAbi,
        int pointerSizeBytes,
        int defaultPack,
        string expectedMessage)
    {
        WriteConfig($$"""
            {
              "scopes": [
                {
                  "name": "native",
                  "paths": ["src/**/*.csproj"],
                  "interop": {
                    "target": {
                      "runtime_identifier": "{{runtimeIdentifier}}",
                      "architecture": "{{architecture}}",
                      "compiler_abi": "{{compilerAbi}}",
                      "pointer_size_bytes": {{pointerSizeBytes}},
                      "default_pack": {{defaultPack}}
                    },
                    "translation_units": [
                      {
                        "path": "native/interop.cpp",
                        "library": "medalgo",
                        "arguments": ["-std=c++20"]
                      }
                    ]
                  }
                }
              ]
            }
            """);

        var act = () => ScopeConfigLoader.Load(_tempDir);

        act.Should().Throw<ScopeConfigException>().WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public void Load_rejects_empty_translation_unit_list()
    {
        WriteConfig(ConfigWithTranslationUnits("[]"));

        var act = () => ScopeConfigLoader.Load(_tempDir);

        act.Should().Throw<ScopeConfigException>().WithMessage("*translation_units*at least one*");
    }

    [Theory]
    [InlineData(
        """{ "path": "C:/native.cpp", "library": "medalgo", "arguments": ["-std=c++20"] }""",
        "path")]
    [InlineData(
        """{ "path": "native/../interop.cpp", "library": "medalgo", "arguments": ["-std=c++20"] }""",
        "path")]
    [InlineData(
        """{ "path": "native/interop.cpp", "library": " ", "arguments": ["-std=c++20"] }""",
        "library")]
    [InlineData(
        """{ "path": "native/interop.cpp", "library": "medalgo", "arguments": [] }""",
        "arguments")]
    [InlineData(
        """{ "path": "native/interop.cpp", "library": "medalgo", "arguments": [" "] }""",
        "arguments[0]")]
    [InlineData(
        """{ "path": "native/interop.cpp", "library": "medalgo", "arguments": ["-std=c++20"], "binary_path": "../medalgo.dll" }""",
        "binary_path")]
    public void Load_rejects_invalid_translation_unit_values(
        string translationUnit,
        string expectedMessage)
    {
        WriteConfig(ConfigWithTranslationUnits($"[{translationUnit}]"));

        var act = () => ScopeConfigLoader.Load(_tempDir);

        act.Should().Throw<ScopeConfigException>().WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public void Load_rejects_duplicate_translation_unit_paths()
    {
        WriteConfig(ConfigWithTranslationUnits("""
            [
              {
                "path": "native/interop.cpp",
                "library": "medalgo",
                "arguments": ["-std=c++20"]
              },
              {
                "path": "native\\interop.cpp",
                "library": "medalgo",
                "arguments": ["-DMEDALGO_EXPORTS"]
              }
            ]
            """));

        var act = () => ScopeConfigLoader.Load(_tempDir);

        act.Should().Throw<ScopeConfigException>().WithMessage("*duplicates translation unit*");
    }

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Join(_tempDir, ScopeConfigLoader.FileName), json);

    private static string ConfigWithTranslationUnits(string translationUnits) =>
        $$"""
          {
            "scopes": [
              {
                "name": "native",
                "paths": ["src/**/*.csproj"],
                "interop": {
                  "target": {
                    "runtime_identifier": "win-x64",
                    "architecture": "x64",
                    "compiler_abi": "msvc",
                    "pointer_size_bytes": 8,
                    "default_pack": 8
                  },
                  "translation_units": {{translationUnits}}
                }
              }
            ]
          }
          """;
}
