using DevBitsLab.Mcp.SourceGraph.Watcher;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class SolutionWatcherTests
{
    private static readonly TimeSpan _shortDebounce = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan _eventTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DefaultExtensions_emitCsAndXaml_butExcludePrivacyDirectoriesCaseInsensitively()
    {
        var root = MakeTempRoot();
        try
        {
            var excludedPaths = new[]
            {
                CreateParentedPath(root, "pAtIeNtDaTa", "Patient.cs"),
                CreateParentedPath(root, "pAtIeNtDaTa", "Patient.xaml"),
                CreateParentedPath(root, "iMaGeS", "Preview.cs"),
                CreateParentedPath(root, "iMaGeS", "Preview.xaml"),
                CreateParentedPath(root, "dEbUg", "Generated.cs"),
                CreateParentedPath(root, "dEbUg", "Generated.xaml"),
            };
            var ordinaryCs = CreateParentedPath(root, "src", "Service.cs");
            var ordinaryXaml = CreateParentedPath(root, "Views", "MainWindow.xaml");

            await using var watcher = new SolutionWatcher(root, debounce: _shortDebounce);

            foreach (var excludedPath in excludedPaths)
            {
                await File.WriteAllTextAsync(excludedPath, "private");
            }
            await Task.Delay(_shortDebounce * 3);
            await File.WriteAllTextAsync(ordinaryCs, "service");
            await File.WriteAllTextAsync(ordinaryXaml, "<Window />");

            var observed = await ReadPathsUntilAsync(
                watcher,
                paths => paths.Contains(ordinaryCs) && paths.Contains(ordinaryXaml),
                _eventTimeout);

            observed.Should().BeEquivalentTo([ordinaryCs, ordinaryXaml]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task GitHeadChange_stillEmitsGitHeadChanged()
    {
        var root = MakeTempRoot();
        try
        {
            var headPath = CreateParentedPath(root, ".git", "HEAD");
            await File.WriteAllTextAsync(headPath, "ref: refs/heads/main");

            await using var watcher = new SolutionWatcher(root, debounce: _shortDebounce);
            await File.WriteAllTextAsync(headPath, "ref: refs/heads/feature");

            using var cts = new CancellationTokenSource(_eventTimeout);
            FileChangeBatch? observed = null;
            try
            {
                await foreach (var batch in watcher.ReadAllAsync(cts.Token))
                {
                    if (batch.Reason == FileChangeReason.GitHeadChanged)
                    {
                        observed = batch;
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Let the assertion below report a missing event.
            }

            observed.Should().NotBeNull();
            observed!.Paths.Should().BeEmpty();
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Rename_emitsBothOldAndNewSourcePaths()
    {
        var root = MakeTempRoot();
        try
        {
            var oldPath = CreateParentedPath(root, "Views", "Before.xaml");
            var newPath = Path.Join(Path.GetDirectoryName(oldPath)!, "After.xaml");
            await File.WriteAllTextAsync(oldPath, "<Window />");

            await using var watcher = new SolutionWatcher(root, debounce: _shortDebounce);
            File.Move(oldPath, newPath);

            var observed = await ReadPathsUntilAsync(
                watcher,
                paths => paths.Contains(oldPath) && paths.Contains(newPath),
                _eventTimeout);

            observed.Should().BeEquivalentTo([oldPath, newPath]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_filtersEachEndpointIndependently(bool moveOutOfExcludedDirectory)
    {
        var root = MakeTempRoot();
        try
        {
            var includedPath = CreateParentedPath(root, "src", "Bridge.cs");
            var excludedPath = CreateParentedPath(root, "PaTiEnTdAtA", "Bridge.cs");
            var oldPath = moveOutOfExcludedDirectory ? excludedPath : includedPath;
            var newPath = moveOutOfExcludedDirectory ? includedPath : excludedPath;
            await File.WriteAllTextAsync(oldPath, "bridge");

            await using var watcher = new SolutionWatcher(root, debounce: _shortDebounce);
            File.Move(oldPath, newPath);

            var observed = await ReadPathsUntilAsync(
                watcher,
                paths => paths.Contains(includedPath),
                _eventTimeout);

            observed.Should().BeEquivalentTo([includedPath]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task DeleteXaml_emitsDeletedPath()
    {
        var root = MakeTempRoot();
        try
        {
            var xamlPath = CreateParentedPath(root, "Views", "Removed.xaml");
            await File.WriteAllTextAsync(xamlPath, "<Page />");

            await using var watcher = new SolutionWatcher(root, debounce: _shortDebounce);
            File.Delete(xamlPath);

            var observed = await ReadPathsUntilAsync(
                watcher,
                paths => paths.Contains(xamlPath),
                _eventTimeout);

            observed.Should().BeEquivalentTo([xamlPath]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ScopeExclude_filtersChangeAndDeletePaths()
    {
        var root = MakeTempRoot();
        try
        {
            var excludedChanged = CreateParentedPath(root, "src", "generated", "Changed.cs");
            var excludedDeleted = CreateParentedPath(root, "src", "Generated", "Deleted.xaml");
            var includedChanged = CreateParentedPath(root, "src", "Changed.cs");
            await File.WriteAllTextAsync(excludedChanged, "before");
            await File.WriteAllTextAsync(excludedDeleted, "<Page />");
            await File.WriteAllTextAsync(includedChanged, "before");

            await using var watcher = new SolutionWatcher(
                root,
                debounce: _shortDebounce,
                logger: null,
                sourceExtensions: null,
                excludePatterns: ["**/generated/**"]);

            await File.WriteAllTextAsync(excludedChanged, "after");
            File.Delete(excludedDeleted);
            await Task.Delay(_shortDebounce * 3);
            await File.WriteAllTextAsync(includedChanged, "after");

            var observed = await ReadPathsUntilAsync(
                watcher,
                paths => paths.Contains(includedChanged),
                _eventTimeout);

            observed.Should().BeEquivalentTo([includedChanged]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ScopeExclude_matchingSyntheticProbe_doesNotRemoveTheExtensionGlobally()
    {
        var root = MakeTempRoot();
        try
        {
            var excludedProbePath = Path.Join(root, "source.cs");
            var includedPath = CreateParentedPath(root, "src", "Other.cs");

            await using var watcher = new SolutionWatcher(
                root,
                debounce: _shortDebounce,
                logger: null,
                sourceExtensions: [".cs"],
                excludePatterns: ["**/source.cs"]);

            await File.WriteAllTextAsync(excludedProbePath, "excluded");
            await Task.Delay(_shortDebounce * 3);
            await File.WriteAllTextAsync(includedPath, "included");

            var observed = await ReadPathsUntilAsync(
                watcher,
                paths => paths.Contains(includedPath),
                _eventTimeout);

            observed.Should().BeEquivalentTo([includedPath]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [SkippableFact]
    public async Task DirectoryLinkOutsideRepository_neverEmitsSourcePath()
    {
        var root = MakeTempRoot();
        var outside = MakeTempRoot();
        try
        {
            var linkedDirectory = Path.Join(root, "src", "External");
            Directory.CreateDirectory(Path.GetDirectoryName(linkedDirectory)!);
            Skip.IfNot(
                PhysicalPathTestSupport.TryCreateDirectoryLink(linkedDirectory, outside),
                "This environment does not permit symbolic-link or junction creation.");
            var linkedPath = Path.Join(linkedDirectory, "Outside.cs");
            var allowedPath = CreateParentedPath(root, "src", "Allowed.cs");

            await using var watcher = new SolutionWatcher(root, debounce: _shortDebounce);
            await File.WriteAllTextAsync(linkedPath, "outside");
            await Task.Delay(_shortDebounce * 3);
            await File.WriteAllTextAsync(allowedPath, "allowed");

            var observed = await ReadPathsUntilAsync(
                watcher,
                paths => paths.Contains(allowedPath),
                _eventTimeout);

            observed.Should().BeEquivalentTo([allowedPath]);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteTempRoot(outside);
        }
    }

    [Fact]
    public async Task ConfiguredExtensions_replaceDefaults_butCannotBypassPrivacyPolicy()
    {
        var root = MakeTempRoot();
        try
        {
            var csPath = CreateParentedPath(root, "src", "Ignored.cs");
            var imagePath = CreateParentedPath(root, "src", "Sensitive.PNG");
            var protoPath = CreateParentedPath(root, "contracts", "service.proto");

            await using var watcher = new SolutionWatcher(
                root,
                debounce: _shortDebounce,
                sourceExtensions: ["proto", ".png"]);

            await File.WriteAllTextAsync(csPath, "ignored");
            await File.WriteAllTextAsync(imagePath, "sensitive");
            await Task.Delay(_shortDebounce * 3);
            await File.WriteAllTextAsync(protoPath, "syntax = \"proto3\";");

            var observed = await ReadPathsUntilAsync(
                watcher,
                paths => paths.Contains(protoPath),
                _eventTimeout);

            observed.Should().BeEquivalentTo([protoPath]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ConfiguredExtensions_rejectMedicalImageOnlyWatchSet()
    {
        var root = MakeTempRoot();
        try
        {
            var act = () => new SolutionWatcher(root, sourceExtensions: [".DCM", ".jpg", ".PNG"]);

            act.Should().Throw<ArgumentException>()
                .WithParameterName("sourceExtensions");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string MakeTempRoot()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "solution-watcher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateParentedPath(string root, string directory, string fileName)
    {
        var parent = Path.Join(root, directory);
        Directory.CreateDirectory(parent);
        return Path.Join(parent, fileName);
    }

    private static string CreateParentedPath(
        string root,
        string directory,
        string subdirectory,
        string fileName)
    {
        var parent = Path.Join(root, directory, subdirectory);
        Directory.CreateDirectory(parent);
        return Path.Join(parent, fileName);
    }

    private static async Task<HashSet<string>> ReadPathsUntilAsync(
        SolutionWatcher watcher,
        Func<HashSet<string>, bool> predicate,
        TimeSpan timeout)
    {
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var batch in watcher.ReadAllAsync(cts.Token))
            {
                foreach (var path in batch.Paths)
                {
                    observed.Add(path);
                }

                if (predicate(observed))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Return the partial set so the assertion reports the missing paths.
        }

        return observed;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup for delayed Windows file-system handles.
        }
    }
}
