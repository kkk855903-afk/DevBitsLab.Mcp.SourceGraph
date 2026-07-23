using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for <see cref="SourceTreeWalker.WalkAsync"/>: discovers non-ignored files, applies the
/// shared exclusion list, computes correct SHA-256, and respects the <c>maxFiles</c> cap.
/// </summary>
public sealed class SourceTreeWalkerTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _externalDirectories = [];

    public SourceTreeWalkerTests()
    {
        _root = Path.Join(Path.GetTempPath(), "sourcegraph-walker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        foreach (var path in _externalDirectories)
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Walk_yields_only_nonIgnoredFiles_withCorrectSha()
    {
        // Plant 5 files: 3 valid under the root + a `obj/` subdir + a `.git/` subdir.
        // Note: each path segment is its own Path.Join arg so the platform-native separator
        // wins. `Path.Join(root, "src/B.cs")` would leave the `/` literal on Windows; the
        // walker enumerates with `\` and the equivalence check would mismatch the `/` vs `\`.
        var (kept1, kept1Sha) = await Plant(Path.Join(_root, "A.cs"), "class A {}");
        var (kept2, kept2Sha) = await Plant(Path.Join(_root, "src", "B.cs"), "class B {}");
        var (kept3, kept3Sha) = await Plant(Path.Join(_root, "src", "sub", "C.cs"), "class C {}");
        await Plant(Path.Join(_root, "obj", "Debug", "D.cs"), "class D {}");
        await Plant(Path.Join(_root, ".git", "HEAD"), "ref: refs/heads/main");

        var outcome = await SourceTreeWalker.WalkAsync(_root, maxFiles: 100);

        outcome.HitLimit.Should().BeFalse();
        outcome.Entries.Should().HaveCount(3);
        outcome.Entries.Select(e => e.Path).Should().BeEquivalentTo(new[] { kept1, kept2, kept3 });
        outcome.Entries.Single(e => e.Path == kept1).Sha256.Should().Equal(kept1Sha);
        outcome.Entries.Single(e => e.Path == kept2).Sha256.Should().Equal(kept2Sha);
        outcome.Entries.Single(e => e.Path == kept3).Sha256.Should().Equal(kept3Sha);
    }

    [Fact]
    public async Task Walk_appliesMedicalPrivacyPolicy_beforeHashingOrCountingFiles()
    {
        var (kept, keptSha) = await Plant(Path.Join(_root, "src", "Allowed.cs"), "class Allowed {}");
        await Plant(Path.Join(_root, "pAtIeNtDaTa", "patient-canary.cs"), "PATIENT-CANARY");
        await Plant(Path.Join(_root, "Images", "scan.cs"), "IMAGE-CANARY");
        await Plant(Path.Join(_root, "Database", "records.sql"), "DATABASE-CANARY");
        await Plant(Path.Join(_root, "Logs", "device.log"), "LOG-CANARY");
        await Plant(Path.Join(_root, "src", "scan.DCM"), "DICOM-CANARY");
        await Plant(Path.Join(_root, "src", "photo.JpG"), "IMAGE-CANARY");

        var outcome = await SourceTreeWalker.WalkAsync(_root, maxFiles: 1);

        outcome.HitLimit.Should().BeFalse(
            "privacy-excluded files must not count toward the traversal cap");
        outcome.Entries.Should().ContainSingle();
        outcome.Entries[0].Path.Should().Be(kept);
        outcome.Entries[0].Sha256.Should().Equal(keptSha);
    }

    [Fact]
    public async Task Walk_appliesScopeExcludePatterns_beforeHashingOrCountingFiles()
    {
        var (kept, keptSha) = await Plant(Path.Join(_root, "src", "Allowed.cs"), "class Allowed {}");
        await Plant(Path.Join(_root, "src", "Generated", "Hidden.cs"), "SCOPE-EXCLUDE-CANARY");

        var outcome = await SourceTreeWalker.WalkAsync(
            _root,
            maxFiles: 1,
            excludePatterns: ["**/generated/**"],
            ct: CancellationToken.None);

        outcome.HitLimit.Should().BeFalse(
            "scope-excluded files must not count toward the traversal cap");
        outcome.Entries.Should().ContainSingle();
        outcome.Entries[0].Path.Should().Be(kept);
        outcome.Entries[0].Sha256.Should().Equal(keptSha);
    }

    [SkippableFact]
    public async Task Walk_neverFollowsDirectoryLinkOutsideRepository()
    {
        var (kept, keptSha) = await Plant(
            Path.Join(_root, "src", "Allowed.cs"),
            "class Allowed {}");
        var outside = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-walker-outside-" + Guid.NewGuid().ToString("N"));
        _externalDirectories.Add(outside);
        await Plant(Path.Join(outside, "Outside.cs"), "OUTSIDE-CANARY");

        var link = Path.Join(_root, "src", "External");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(link, outside),
            "This environment does not permit symbolic-link creation.");

        var outcome = await SourceTreeWalker.WalkAsync(_root, maxFiles: 1);

        outcome.HitLimit.Should().BeFalse(
            "a file reachable only through an out-of-repository link must not count toward the cap");
        outcome.Entries.Should().ContainSingle();
        outcome.Entries[0].Path.Should().Be(kept);
        outcome.Entries[0].Sha256.Should().Equal(keptSha);
    }

    [Fact]
    public async Task Walk_respects_maxFiles_cap_and_setsHitLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await Plant(Path.Join(_root, $"F{i}.cs"), $"class F{i} {{}}");
        }
        var outcome = await SourceTreeWalker.WalkAsync(_root, maxFiles: 2);

        outcome.Entries.Should().HaveCount(2);
        outcome.HitLimit.Should().BeTrue("the tree had 5 files but the cap was 2");
    }

    [Fact]
    public async Task Walk_treeMatchesCapExactly_doesNotSetHitLimit()
    {
        // Regression for the partial-flag false-positive: a tree with exactly maxFiles entries
        // is NOT truncated. Walker must distinguish "tree fit within cap" from "tree exceeded".
        for (var i = 0; i < 3; i++)
        {
            await Plant(Path.Join(_root, $"F{i}.cs"), $"class F{i} {{}}");
        }
        var outcome = await SourceTreeWalker.WalkAsync(_root, maxFiles: 3);

        outcome.Entries.Should().HaveCount(3);
        outcome.HitLimit.Should().BeFalse("the tree had exactly 3 files; the cap was reached but not exceeded");
    }

    [Fact]
    public async Task Walk_emptyDirectory_yieldsNothing()
    {
        var outcome = await SourceTreeWalker.WalkAsync(_root, maxFiles: 100);
        outcome.Entries.Should().BeEmpty();
        outcome.HitLimit.Should().BeFalse();
    }

    [Fact]
    public async Task Walk_missingDirectory_yieldsNothing()
    {
        var outcome = await SourceTreeWalker.WalkAsync(Path.Join(_root, "does-not-exist"), maxFiles: 100);
        outcome.Entries.Should().BeEmpty();
        outcome.HitLimit.Should().BeFalse();
    }

    private static async Task<(string Path, byte[] Sha)> Plant(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(contents);
        await File.WriteAllBytesAsync(path, bytes);
        return (path, SHA256.HashData(bytes));
    }

}
