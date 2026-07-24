using System.Buffers.Binary;
using System.Text.Json;
using System.Runtime.InteropServices;
using DevBitsLab.Mcp.SourceGraph.Core.Security;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ExecutionTrustPolicyTests : IDisposable
{
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private readonly string _tempRoot = Path.Join(
        Path.GetTempPath(),
        "medinterop-execution-trust-" + Guid.NewGuid().ToString("N"));
    private readonly string _repositoryRoot;
    private readonly string _userRoot;
    private readonly string _trustFile;
    private readonly List<string> _links = [];

    public ExecutionTrustPolicyTests()
    {
        _repositoryRoot = Path.Join(_tempRoot, "repo");
        _userRoot = Path.Join(_tempRoot, "user");
        _trustFile = Path.Join(_userRoot, "MedInteropLens", "trust-v1.json");
        Directory.CreateDirectory(_repositoryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(_trustFile)!);
    }

    [Fact]
    public void DefaultTrustPath_is_underLocalApplicationData()
    {
        var localApplicationData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        UserExecutionTrustPolicy.DefaultTrustFilePath.Should().Be(
            Path.Join(localApplicationData, "MedInteropLens", "trust-v1.json"));
    }

    [Fact]
    public void EmptyTrustDocument_deniesEveryExecutableCapability()
    {
        WriteTrust(EmptyTrustDocument());
        var policy = new UserExecutionTrustPolicy(_trustFile);

        foreach (var capability in Enum.GetValues<ExecutionCapability>())
        {
            var decision = capability is ExecutionCapability.MsBuildEvaluation
                or ExecutionCapability.ProjectSourceGenerators
                or ExecutionCapability.NativeParsing
                ? policy.EvaluateRepositoryCapability(
                    _repositoryRoot,
                    capability)
                : policy.EvaluateNuGetPluginCapability(
                    _repositoryRoot,
                    "Not.Restored.Plugin",
                    "1.0.0",
                    capability);

            decision.IsAllowed.Should().BeFalse();
            decision.Reason.Should().Be(
                capability is ExecutionCapability.MsBuildEvaluation
                    or ExecutionCapability.ProjectSourceGenerators
                    or ExecutionCapability.NativeParsing
                    ? ExecutionTrustReason.RepositoryNotTrusted
                    : ExecutionTrustReason.NuGetPluginNotTrusted);
        }
    }

    [Fact]
    public void NativeParsing_requires_its_own_repository_grant()
    {
        WriteTrust(
            RepositoryTrustDocument(
                _repositoryRoot,
                "MsBuildEvaluation",
                "NativeParsing"));
        var policy = new UserExecutionTrustPolicy(_trustFile);

        policy.EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.NativeParsing)
            .IsAllowed.Should().BeTrue();
        policy.EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.ProjectSourceGenerators)
            .IsAllowed.Should().BeFalse(
                "native parsing must not imply source-generator execution");
    }

    [Fact]
    public void MissingTrustFile_failsClosed_withMachineReadableReason()
    {
        var decision = new UserExecutionTrustPolicy(_trustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(ExecutionTrustReason.TrustFileMissing);
        decision.ReasonCode.Should().Be("trust-file-missing");
    }

    [Fact]
    public void MalformedTrustFile_failsClosed()
    {
        File.WriteAllText(_trustFile, """{"schemaVersion":1,"repositories":[""");

        var decision = new UserExecutionTrustPolicy(_trustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(ExecutionTrustReason.TrustFileMalformed);
    }

    [Fact]
    public void UnreadableTrustPath_failsClosed()
    {
        var directoryPath = Path.Join(_userRoot, "trust-as-directory");
        Directory.CreateDirectory(directoryPath);

        var decision = new UserExecutionTrustPolicy(directoryPath)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(ExecutionTrustReason.TrustFileReadFailed);
    }

    [Fact]
    public void UnsupportedTrustSchema_failsClosed()
    {
        WriteTrust(
            new
            {
                schemaVersion = 2,
                repositories = Array.Empty<object>(),
                pathPlugins = Array.Empty<object>(),
                nugetPlugins = Array.Empty<object>(),
            });

        var decision = new UserExecutionTrustPolicy(_trustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(
            ExecutionTrustReason.TrustSchemaUnsupported);
    }

    [Fact]
    public void InvalidGrantShape_failsClosed_forTheWholeDocument()
    {
        WriteTrust(
            new
            {
                schemaVersion = 1,
                repositories = new[]
                {
                    new
                    {
                        path = _repositoryRoot,
                        capabilities = new[] { "MsBuildEvaluation" },
                        unexpected = true,
                    },
                },
                pathPlugins = Array.Empty<object>(),
                nugetPlugins = Array.Empty<object>(),
            });

        var decision = new UserExecutionTrustPolicy(_trustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(ExecutionTrustReason.TrustFileMalformed);
    }

    [Fact]
    public void DuplicateJsonProperty_failsClosed_insteadOfUsingLastValue()
    {
        File.WriteAllText(
            _trustFile,
            $$"""
              {
                "schemaVersion": 2,
                "schemaVersion": 1,
                "repositories": [
                  {
                    "path": {{JsonSerializer.Serialize(_repositoryRoot)}},
                    "capabilities": ["MsBuildEvaluation"]
                  }
                ],
                "pathPlugins": [],
                "nugetPlugins": []
              }
              """);

        var decision = new UserExecutionTrustPolicy(_trustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.Reason.Should().Be(ExecutionTrustReason.TrustFileMalformed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData(" MsBuildEvaluation ")]
    [InlineData("MsBuildEvaluation, ProjectSourceGenerators")]
    public void CapabilityNames_mustMatchTheExactSchemaToken(string capability)
    {
        WriteTrust(RepositoryTrustDocument(_repositoryRoot, capability));

        var decision = new UserExecutionTrustPolicy(_trustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.Reason.Should().Be(ExecutionTrustReason.TrustFileMalformed);
    }

    [Fact]
    public void DuplicateNormalizedGrantSubjects_failTheWholeTrustDocument()
    {
        WriteTrust(
            new
            {
                schemaVersion = 1,
                repositories = new[]
                {
                    new
                    {
                        path = _repositoryRoot,
                        capabilities = new[] { "MsBuildEvaluation" },
                    },
                    new
                    {
                        path = Path.Join(_repositoryRoot, "."),
                        capabilities = new[] { "ProjectSourceGenerators" },
                    },
                },
                pathPlugins = new[]
                {
                    new
                    {
                        fingerprint = "sha256:" + new string('a', 64),
                        capabilities = new[] { "PluginAnalyzer" },
                    },
                    new
                    {
                        fingerprint = "sha256:" + new string('A', 64),
                        capabilities = new[] { "PluginTool" },
                    },
                },
                nugetPlugins = new[]
                {
                    new
                    {
                        packageId = "Contoso.Plugin",
                        version = "1.2.3",
                        capabilities = new[] { "PluginAnalyzer" },
                    },
                    new
                    {
                        packageId = "contoso.plugin",
                        version = "1.2.3",
                        capabilities = new[] { "PluginTool" },
                    },
                },
            });

        var decision = new UserExecutionTrustPolicy(_trustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.Reason.Should().Be(ExecutionTrustReason.TrustFileMalformed);
    }

    [Fact]
    public void RepositoryCannotSelfTrust_fromSourcegraphDirectory()
    {
        var forgedTrustFile = Path.Join(
            _repositoryRoot,
            ".sourcegraph",
            "trust-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(forgedTrustFile)!);
        WriteTrust(
            forgedTrustFile,
            RepositoryTrustDocument(
                _repositoryRoot,
                "MsBuildEvaluation"));

        var decision = new UserExecutionTrustPolicy(forgedTrustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(
            ExecutionTrustReason.TrustStoreInsideRepository);
    }

    [SkippableFact]
    public void TrustPathWithReparseAncestor_isRejected_evenWhenTargetIsExternal()
    {
        var realTrustDirectory = Path.Join(_userRoot, "real-trust");
        Directory.CreateDirectory(realTrustDirectory);
        var linkedTrustDirectory = Path.Join(_userRoot, "linked-trust");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(
                linkedTrustDirectory,
                realTrustDirectory),
            "This environment does not permit symbolic-link or junction creation.");
        _links.Add(linkedTrustDirectory);
        var linkedTrustFile = Path.Join(linkedTrustDirectory, "trust-v1.json");
        WriteTrust(
            Path.Join(realTrustDirectory, "trust-v1.json"),
            RepositoryTrustDocument(
                _repositoryRoot,
                "MsBuildEvaluation"));

        var decision = new UserExecutionTrustPolicy(linkedTrustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(
            ExecutionTrustReason.TrustStoreContainsReparsePoint);
        decision.ReasonCode.Should().Be(
            "trust-store-contains-reparse-point");
    }

    [SkippableFact]
    public void TrustFileHardLinkedIntoRepository_isRejected()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Hard-link metadata coverage is Windows-specific.");
        WriteTrust(
            RepositoryTrustDocument(
                _repositoryRoot,
                "MsBuildEvaluation"));
        var repositoryAlias = Path.Join(_repositoryRoot, "forged-trust.json");
        Skip.IfNot(
            CreateHardLinkW(repositoryAlias, _trustFile, IntPtr.Zero),
            "The test filesystem does not permit hard-link creation.");

        var decision = new UserExecutionTrustPolicy(_trustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(
            ExecutionTrustReason.TrustBoundaryResolutionFailed);
    }

    [Fact]
    public void RepositoryCapabilities_areIndependent()
    {
        WriteTrust(
            RepositoryTrustDocument(
                _repositoryRoot,
                "MsBuildEvaluation"));
        var policy = new UserExecutionTrustPolicy(_trustFile);

        policy.EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation)
            .IsAllowed.Should().BeTrue();
        policy.EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.ProjectSourceGenerators)
            .Reason.Should().Be(ExecutionTrustReason.RepositoryNotTrusted);
        policy.EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.PluginTool)
            .Reason.Should().Be(
                ExecutionTrustReason.CapabilityNotApplicable);
    }

    [Fact]
    public void RepositoryPaths_areCaseInsensitive_onWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteTrust(
            RepositoryTrustDocument(
                _repositoryRoot.ToUpperInvariant(),
                "MsBuildEvaluation"));

        var decision = new UserExecutionTrustPolicy(_trustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot.ToLowerInvariant(),
                ExecutionCapability.MsBuildEvaluation);

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void NuGetTrust_matchesPackageId_caseInsensitively_andVersionExactly()
    {
        WriteTrust(
            NuGetTrustDocument(
                "Contoso.Medical.Plugin",
                "1.2.3",
                "PluginTool"));
        var policy = new UserExecutionTrustPolicy(_trustFile);

        policy.EvaluateNuGetPluginCapability(
                _repositoryRoot,
                "contoso.medical.plugin",
                "1.2.3",
                ExecutionCapability.PluginTool)
            .IsAllowed.Should().BeTrue(
                "authorization is a metadata-only pre-restore decision");
        policy.EvaluateNuGetPluginCapability(
                _repositoryRoot,
                "Contoso.Medical.Plugin",
                "1.2.4",
                ExecutionCapability.PluginTool)
            .Reason.Should().Be(ExecutionTrustReason.NuGetPluginNotTrusted);
        policy.EvaluateNuGetPluginCapability(
                _repositoryRoot,
                "Contoso.Medical.Plugin",
                "1.2.3.0",
                ExecutionCapability.PluginTool)
            .Reason.Should().Be(ExecutionTrustReason.NuGetPluginNotTrusted);
        policy.EvaluateNuGetPluginCapability(
                _repositoryRoot,
                "Contoso.Medical.Plugin",
                "1.2.3",
                ExecutionCapability.PluginAnalyzer)
            .Reason.Should().Be(ExecutionTrustReason.NuGetPluginNotTrusted);
        policy.EvaluateNuGetPluginCapability(
                _repositoryRoot,
                "Contoso.Medical.Plugin",
                "1.*",
                ExecutionCapability.PluginTool)
            .Reason.Should().Be(ExecutionTrustReason.InvalidRequest);
    }

    [Theory]
    [InlineData(Architecture.X64, 144, 24, 16)]
    [InlineData(Architecture.Arm64, 128, 16, 20)]
    public void LinuxStatAbiLayouts_decodeOfficialGlibcFields(
        Architecture architecture,
        int nativeSize,
        int modeOffset,
        int linkCountOffset)
    {
        const uint mode = 0x81A4;
        const long size = 0x0102030405060708;
        const long modifiedSeconds = 1_721_234_567;
        const long modifiedNanoseconds = 987_654_321;
        var nativeBytes = new byte[nativeSize];

        BinaryPrimitives.WriteUInt32LittleEndian(
            nativeBytes.AsSpan(modeOffset),
            mode);
        if (architecture == Architecture.X64)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                nativeBytes.AsSpan(linkCountOffset),
                7);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                nativeBytes.AsSpan(linkCountOffset),
                7);
            BinaryPrimitives.WriteUInt32LittleEndian(
                nativeBytes.AsSpan(24),
                0xDEADBEEF);
        }
        BinaryPrimitives.WriteInt64LittleEndian(nativeBytes.AsSpan(48), size);
        BinaryPrimitives.WriteInt64LittleEndian(
            nativeBytes.AsSpan(88),
            modifiedSeconds);
        BinaryPrimitives.WriteInt64LittleEndian(
            nativeBytes.AsSpan(96),
            modifiedNanoseconds);

        var decoded =
            PathPluginBundleFingerprint.DecodeLinuxStatForValidation(
                nativeBytes,
                architecture);

        decoded.Mode.Should().Be(mode);
        decoded.LinkCount.Should().Be(7);
        decoded.Size.Should().Be(size);
        decoded.ModifiedSeconds.Should().Be(modifiedSeconds);
        decoded.ModifiedNanoseconds.Should().Be(modifiedNanoseconds);
    }

    [Fact]
    public void BundleFingerprint_isDeterministic_andCoversEntryDependenciesAndNativeFiles()
    {
        var bundle = CreatePluginBundle();
        var entry = Path.Join(bundle, "Medical.Plugin.dll");

        var first = PathPluginBundleFingerprint.Compute(entry);
        var second = PathPluginBundleFingerprint.Compute(entry);

        first.IsSuccess.Should().BeTrue();
        second.Should().Be(first);
        first.Fingerprint.Should().Be(
            "sha256:d5325829953c417e4e3db874212ac14f0bbaa3d046359f17066d10ea17eb933e",
            "the versioned relative-path and byte framing is a stable contract");

        var dependency = Path.Join(bundle, "Medical.Dependency.dll");
        var native = Path.Join(bundle, "runtimes", "win-x64", "native", "medical.dll");
        var originalDependency = File.ReadAllBytes(dependency);
        var originalNative = File.ReadAllBytes(native);

        File.WriteAllBytes(dependency, [99, 98, 97]);
        PathPluginBundleFingerprint.Compute(entry).Fingerprint
            .Should().NotBe(first.Fingerprint, "dependency byte changes are covered");
        File.WriteAllBytes(dependency, originalDependency);
        PathPluginBundleFingerprint.Compute(entry).Should().Be(first);

        var added = Path.Join(bundle, "added.config");
        File.WriteAllBytes(added, [42]);
        PathPluginBundleFingerprint.Compute(entry).Fingerprint
            .Should().NotBe(first.Fingerprint, "added bundle files are covered");
        File.Delete(added);
        PathPluginBundleFingerprint.Compute(entry).Should().Be(first);

        File.Delete(native);
        PathPluginBundleFingerprint.Compute(entry).Fingerprint
            .Should().NotBe(first.Fingerprint, "removed native files are covered");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllBytes(native, originalNative);
        PathPluginBundleFingerprint.Compute(entry).Should().Be(first);
    }

    [Fact]
    public void BundlePaths_areCaseInsensitive_onWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundle = CreatePluginBundle();
        var canonicalEntry = Path.Join(bundle, "Medical.Plugin.dll");
        var differentlyCasedEntry = canonicalEntry.ToUpperInvariant();
        var differentlyCasedRoot = bundle.ToLowerInvariant();

        PathPluginBundleFingerprint.Compute(
                differentlyCasedEntry,
                differentlyCasedRoot)
            .Should().Be(PathPluginBundleFingerprint.Compute(canonicalEntry));
    }

    [Fact]
    public void PathPluginTrust_requiresMatchingFingerprint_andCapability()
    {
        var bundle = CreatePluginBundle();
        var entry = Path.Join(bundle, "Medical.Plugin.dll");
        var fingerprint =
            PathPluginBundleFingerprint.Compute(entry).Fingerprint!;
        WriteTrust(
            PathPluginTrustDocument(
                fingerprint,
                "PluginLanguageIndexer"));
        var policy = new UserExecutionTrustPolicy(_trustFile);

        policy.EvaluatePathPluginCapability(
                _repositoryRoot,
                entry,
                ExecutionCapability.PluginLanguageIndexer)
            .Should().Match<ExecutionTrustDecision>(
                decision =>
                    !decision.IsAllowed
                    && decision.Reason
                    == ExecutionTrustReason.PathPluginSnapshotRequired
                    && decision.SubjectFingerprint == fingerprint,
                "a mutable path inspection cannot directly authorize execution");
        policy.EvaluatePathPluginCapability(
                _repositoryRoot,
                entry,
                ExecutionCapability.PluginAnalyzer)
            .Reason.Should().Be(ExecutionTrustReason.PathPluginNotTrusted);

        File.WriteAllBytes(
            Path.Join(bundle, "Medical.Dependency.dll"),
            [4, 3, 2, 1]);
        var changed = policy.EvaluatePathPluginCapability(
            _repositoryRoot,
            entry,
            ExecutionCapability.PluginLanguageIndexer);
        changed.IsAllowed.Should().BeFalse();
        changed.Reason.Should().Be(ExecutionTrustReason.PathPluginNotTrusted);
        changed.SubjectFingerprint.Should().NotBe(fingerprint);
    }

    [SkippableFact]
    public void BundleFingerprint_rejectsLinkThatEscapesBundle()
    {
        var bundle = CreatePluginBundle();
        var outside = Path.Join(_tempRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Join(outside, "outside.dll"), [7, 7, 7]);
        var link = Path.Join(bundle, "linked-outside");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(link, outside),
            "This environment does not permit symbolic-link or junction creation.");
        _links.Add(link);

        var result = PathPluginBundleFingerprint.Compute(
            Path.Join(bundle, "Medical.Plugin.dll"));

        result.IsSuccess.Should().BeFalse();
        result.Reason.Should().Be(
            ExecutionTrustReason.PathPluginBundleContainsReparsePoint);
    }

    [SkippableFact]
    public void BundleFingerprint_rejectsReparseCycle()
    {
        var bundle = CreatePluginBundle();
        var link = Path.Join(bundle, "cycle");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(link, bundle),
            "This environment does not permit symbolic-link or junction creation.");
        _links.Add(link);

        var result = PathPluginBundleFingerprint.Compute(
            Path.Join(bundle, "Medical.Plugin.dll"));

        result.IsSuccess.Should().BeFalse();
        result.Reason.Should().Be(
            ExecutionTrustReason.PathPluginBundleContainsReparsePoint);
    }

    [SkippableFact]
    public void BundleFingerprint_rejectsWindowsAlternateDataStreams()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Alternate data stream coverage is Windows-specific.");
        var bundle = CreatePluginBundle();
        var entry = Path.Join(bundle, "Medical.Plugin.dll");
        var alternateStream = entry + ":untrusted";
        try
        {
            File.WriteAllBytes(alternateStream, [13, 37]);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            Skip.If(true, "The test filesystem does not support alternate data streams.");
        }

        var result = PathPluginBundleFingerprint.Compute(entry);

        result.IsSuccess.Should().BeFalse();
        result.Reason.Should().Be(
            ExecutionTrustReason.PathPluginBundleHasUnsupportedFileIdentity);
    }

    [SkippableFact]
    public void BundleFingerprint_rejectsWindowsHardLinks()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Hard-link metadata coverage is Windows-specific.");
        var bundle = CreatePluginBundle();
        var dependency = Path.Join(bundle, "Medical.Dependency.dll");
        var alias = Path.Join(bundle, "Medical.Dependency.Alias.dll");
        Skip.IfNot(
            CreateHardLinkW(alias, dependency, IntPtr.Zero),
            "The test filesystem does not permit hard-link creation.");

        var result = PathPluginBundleFingerprint.Compute(
            Path.Join(bundle, "Medical.Plugin.dll"));

        result.IsSuccess.Should().BeFalse();
        result.Reason.Should().Be(
            ExecutionTrustReason.PathPluginBundleHasUnsupportedFileIdentity);
    }

    [Fact]
    public void Evaluation_neverModifiesTrustFile()
    {
        WriteTrust(
            RepositoryTrustDocument(
                _repositoryRoot,
                "MsBuildEvaluation"));
        var before = File.ReadAllBytes(_trustFile);
        var beforeWriteTime = File.GetLastWriteTimeUtc(_trustFile);

        var decision = new UserExecutionTrustPolicy(_trustFile)
            .EvaluateRepositoryCapability(
                _repositoryRoot,
                ExecutionCapability.MsBuildEvaluation);

        decision.IsAllowed.Should().BeTrue();
        File.ReadAllBytes(_trustFile).Should().Equal(before);
        File.GetLastWriteTimeUtc(_trustFile).Should().Be(beforeWriteTime);
    }

    public void Dispose()
    {
        foreach (var link in _links)
        {
            try
            {
                Directory.Delete(link);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string CreatePluginBundle()
    {
        var bundle = Path.Join(
            _repositoryRoot,
            "plugins",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(
            Path.Join(bundle, "runtimes", "win-x64", "native"));
        File.WriteAllBytes(
            Path.Join(bundle, "Medical.Plugin.dll"),
            [1, 2, 3, 4]);
        File.WriteAllBytes(
            Path.Join(bundle, "Medical.Dependency.dll"),
            [5, 6, 7, 8]);
        File.WriteAllBytes(
            Path.Join(
                bundle,
                "runtimes",
                "win-x64",
                "native",
                "medical.dll"),
            [9, 10, 11, 12]);
        return bundle;
    }

    private void WriteTrust(object document) =>
        WriteTrust(_trustFile, document);

    private static void WriteTrust(string path, object document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object EmptyTrustDocument() =>
        new
        {
            schemaVersion = 1,
            repositories = Array.Empty<object>(),
            pathPlugins = Array.Empty<object>(),
            nugetPlugins = Array.Empty<object>(),
        };

    private static object RepositoryTrustDocument(
        string repositoryPath,
        params string[] capabilities) =>
        new
        {
            schemaVersion = 1,
            repositories = new[]
            {
                new
                {
                    path = repositoryPath,
                    capabilities,
                },
            },
            pathPlugins = Array.Empty<object>(),
            nugetPlugins = Array.Empty<object>(),
        };

    private static object PathPluginTrustDocument(
        string fingerprint,
        params string[] capabilities) =>
        new
        {
            schemaVersion = 1,
            repositories = Array.Empty<object>(),
            pathPlugins = new[]
            {
                new
                {
                    fingerprint,
                    capabilities,
                },
            },
            nugetPlugins = Array.Empty<object>(),
        };

    private static object NuGetTrustDocument(
        string packageId,
        string version,
        params string[] capabilities) =>
        new
        {
            schemaVersion = 1,
            repositories = Array.Empty<object>(),
            pathPlugins = Array.Empty<object>(),
            nugetPlugins = new[]
            {
                new
                {
                    packageId,
                    version,
                    capabilities,
                },
            },
        };
}
