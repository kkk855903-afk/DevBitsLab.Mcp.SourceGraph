using System;
using System.IO;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Covers the four scoping invariants required by add-scoping:
///   - one-shot migration of legacy graph.db
///   - synthesised default scope when no .sourcegraph.json present
///   - schema validation rejects malformed configs
///   - scope id validation
/// </summary>
public sealed class ScopeTests
{
    [Fact]
    public void ScopeIdValidator_acceptsKebabCase()
    {
        ScopeIdValidator.IsValid("default").Should().BeTrue();
        ScopeIdValidator.IsValid("frontend").Should().BeTrue();
        ScopeIdValidator.IsValid("foo-bar-baz").Should().BeTrue();
        ScopeIdValidator.IsValid("a1b2").Should().BeTrue();
    }

    [Fact]
    public void ScopeIdValidator_rejectsBadIds()
    {
        ScopeIdValidator.IsValid("").Should().BeFalse();
        ScopeIdValidator.IsValid("Foo").Should().BeFalse(); // uppercase
        ScopeIdValidator.IsValid("-foo").Should().BeFalse(); // leading hyphen
        ScopeIdValidator.IsValid("foo bar").Should().BeFalse(); // space
        ScopeIdValidator.IsValid(new string('a', 65)).Should().BeFalse(); // too long
        ScopeIdValidator.IsValid("foo/bar").Should().BeFalse(); // slash (filename safety)
    }

    [Fact]
    public void ScopeConfigLoader_synthesisesDefaultWhenNoFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var config = ScopeConfigLoader.Load(tmp);
            config.Scopes.Should().HaveCount(1);
            config.Scopes[0].Id.Should().Be("default");
            config.DefaultScope.Should().Be("default");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_readsMultiScopeJson()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"),
                """
                {
                  "scopes": [
                    { "name": "frontend", "solutions": ["src/frontend.slnx"] },
                    { "name": "backend",  "solutions": ["src/backend.slnx"], "exclude": ["**/Generated/**"] },
                    { "name": "vendor",   "paths": ["third_party/**/*.csproj"], "isolated": true }
                  ],
                  "default_scope": "backend"
                }
                """);
            var config = ScopeConfigLoader.Load(tmp);
            config.Scopes.Should().HaveCount(3);
            config.DefaultScope.Should().Be("backend");
            config.Scopes[0].ProjectSet.Should().BeOfType<ScopeProjectSet.Solutions>();
            config.Scopes[1].ProjectSet.Should().BeOfType<ScopeProjectSet.Solutions>();
            config.Scopes[2].ProjectSet.Should().BeOfType<ScopeProjectSet.Paths>();
            config.Scopes[2].Isolated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_rejectsMalformedJson()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"), "{ this is not json }");
            var act = () => ScopeConfigLoader.Load(tmp);
            act.Should().Throw<ScopeConfigException>().WithMessage("*not valid JSON*");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_rejectsDuplicateNames()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"),
                """
                {
                  "scopes": [
                    { "name": "foo", "solutions": ["a.slnx"] },
                    { "name": "foo", "solutions": ["b.slnx"] }
                  ]
                }
                """);
            var act = () => ScopeConfigLoader.Load(tmp);
            act.Should().Throw<ScopeConfigException>().WithMessage("*more than once*");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_rejectsInvalidId()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"),
                """
                {
                  "scopes": [
                    { "name": "Bad-ID", "solutions": ["a.slnx"] }
                  ]
                }
                """);
            var act = () => ScopeConfigLoader.Load(tmp);
            act.Should().Throw<ScopeConfigException>().WithMessage("*invalid id*");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_rejectsUnknownDefaultScope()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"),
                """
                {
                  "scopes": [{ "name": "foo", "solutions": ["a.slnx"] }],
                  "default_scope": "missing"
                }
                """);
            var act = () => ScopeConfigLoader.Load(tmp);
            act.Should().Throw<ScopeConfigException>().WithMessage("*default_scope*missing*");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeLayout_migratesLegacyDb()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // Stand up a legacy graph.db at the old layout (any non-empty file works for the rename).
            var sourcegraphDir = ScopeLayout.SourcegraphDir(tmp);
            Directory.CreateDirectory(sourcegraphDir);
            var legacy = ScopeLayout.LegacyDbPath(tmp);
            File.WriteAllBytes(legacy, new byte[] { 0x01, 0x02 });

            var migrated = ScopeLayout.MigrateLegacyDb(tmp);

            migrated.Should().BeTrue();
            File.Exists(legacy).Should().BeFalse("legacy file should be moved, not copied");
            File.Exists(ScopeLayout.ScopeDbPath(tmp, "default")).Should().BeTrue();

            // Idempotency: second invocation is a no-op (legacy is already gone).
            ScopeLayout.MigrateLegacyDb(tmp).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeLayout_skipsMigrationWhenDestinationExists()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // Both legacy and new exist: don't clobber the new file.
            var sourcegraphDir = ScopeLayout.SourcegraphDir(tmp);
            Directory.CreateDirectory(ScopeLayout.ScopesDirectory(tmp));
            File.WriteAllBytes(ScopeLayout.LegacyDbPath(tmp), new byte[] { 0x01 });
            File.WriteAllBytes(ScopeLayout.ScopeDbPath(tmp, "default"), new byte[] { 0x02, 0x03 });

            var migrated = ScopeLayout.MigrateLegacyDb(tmp);

            migrated.Should().BeFalse("destination already exists");
            File.Exists(ScopeLayout.LegacyDbPath(tmp)).Should().BeTrue("legacy preserved");
            File.ReadAllBytes(ScopeLayout.ScopeDbPath(tmp, "default")).Length.Should().Be(2, "destination unchanged");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task ScopeRegistry_persistsAndLoadsRows()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var dbPath = Path.Combine(tmp, "_meta.db");
            await using var registry = new SqliteScopeRegistry(dbPath);
            await registry.EnsureSchemaAsync();

            await registry.UpsertAsync(new ScopeRow(
                Id: "foo",
                Name: "foo",
                Root: tmp,
                ProjectSetJson: "{\"Kind\":\"solutions\",\"Items\":[\"a.slnx\"],\"Exclude\":null}",
                Isolated: false,
                LastIndexedAt: DateTimeOffset.UtcNow,
                Status: "ok"));

            var rows = await registry.ListAsync();
            rows.Should().HaveCount(1);
            rows[0].Id.Should().Be("foo");

            var single = await registry.GetAsync("foo");
            single.Should().NotBeNull();
            single!.Status.Should().Be("ok");

            await registry.RemoveAsync("foo");
            (await registry.ListAsync()).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
