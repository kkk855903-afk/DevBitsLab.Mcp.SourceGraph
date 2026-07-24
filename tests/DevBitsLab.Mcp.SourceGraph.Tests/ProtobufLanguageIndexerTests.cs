using System.Text;
using DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ProtobufLanguageIndexerTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "sourcegraph-protobuf-indexer-" + Guid.NewGuid().ToString("N"));

    public ProtobufLanguageIndexerTests() =>
        Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Fixture_emits_nested_message_field_oneof_and_streaming_rpc_facts()
    {
        var fixtureRoot = LocateFixture("ProtoContracts");
        var path = Path.Join(fixtureRoot, "medical.proto");
        var events = await new ProtobufLanguageIndexer().IndexAsync(
            new IndexContext(
                path,
                await File.ReadAllBytesAsync(path),
                "test",
                fixtureRoot),
            CancellationToken.None);

        var symbols = events.OfType<IndexEvent.SymbolDeclared>().ToArray();
        symbols.Should().HaveCount(13);
        symbols.Select(symbol => symbol.CanonicalKey).Should().Contain(
        [
            "proto:M:medical.imaging.v1.ScanRequest",
            "proto:M:medical.imaging.v1.ScanRequest.Frame",
            "proto:M:medical.imaging.v1.ScanReply",
            "proto:F:medical.imaging.v1.ScanRequest.patient_id",
            "proto:F:medical.imaging.v1.ScanRequest.frames",
            "proto:F:medical.imaging.v1.ScanRequest.dicom",
            "proto:F:medical.imaging.v1.ScanRequest.study_uid",
            "proto:F:medical.imaging.v1.ScanRequest.Frame.payload",
            "proto:F:medical.imaging.v1.ScanReply.accepted_frames",
            "proto:R:medical.imaging.v1.Scanner.Upload",
            "proto:R:medical.imaging.v1.Scanner.Watch",
            "proto:R:medical.imaging.v1.Scanner.Duplex",
            "proto:R:medical.imaging.v1.Scanner.Ping",
        ]);

        symbols.Single(symbol =>
                symbol.CanonicalKey
                == "proto:M:medical.imaging.v1.ScanRequest.Frame")
            .ContainerCanonicalKey.Should()
            .Be("proto:M:medical.imaging.v1.ScanRequest");
        symbols.Single(symbol =>
                symbol.CanonicalKey
                == "proto:F:medical.imaging.v1.ScanRequest.dicom")
            .ContainerCanonicalKey.Should()
            .Be("proto:M:medical.imaging.v1.ScanRequest");
        symbols.Should().OnlyContain(symbol =>
            symbol.StartLine > 0
            && symbol.StartColumn > 0
            && symbol.EndLine >= symbol.StartLine
            && symbol.EndColumn > 0);

        var facts = events
            .OfType<IndexEvent.AnnotationAttached>()
            .Select(annotation =>
            {
                annotation.Flavor.Should().Be(ProtoContractAnnotations.Flavor);
                annotation.ArgsJson.Should().NotBeNull();
                return ProtoContractPayloadCodec.Decode(annotation.ArgsJson!);
            })
            .ToArray();
        facts.Should().HaveCount(13);
        facts.Should().OnlyContain(fact =>
            fact.Status == ProtoContractStatus.Partial
            && fact.ImportCount == 1
            && fact.IncompleteReasons.SequenceEqual(
                new[]
                {
                    ProtoContractPayloadCodec.ImportsNotResolvedReason,
                }));

        var oneof = facts.Single(fact =>
            fact.SymbolCanonicalKey
            == "proto:F:medical.imaging.v1.ScanRequest.dicom");
        oneof.Field.Should().NotBeNull();
        oneof.Field!.Number.Should().Be(3);
        oneof.Field.Type.Should().Be("bytes");
        oneof.Field.OneofName.Should().Be("source");
        oneof.Field.Cardinality.Should().Be(ProtoFieldCardinality.Singular);

        var upload = facts.Single(fact =>
            fact.SymbolCanonicalKey
            == "proto:R:medical.imaging.v1.Scanner.Upload");
        upload.Rpc.Should().NotBeNull();
        upload.Rpc!.InputType.Should().Be("ScanRequest");
        upload.Rpc.OutputType.Should().Be("ScanReply");
        upload.Rpc.ClientStreaming.Should().BeTrue();
        upload.Rpc.ServerStreaming.Should().BeFalse();

        var duplex = facts.Single(fact =>
            fact.SymbolCanonicalKey
            == "proto:R:medical.imaging.v1.Scanner.Duplex");
        duplex.Rpc!.ClientStreaming.Should().BeTrue();
        duplex.Rpc.ServerStreaming.Should().BeTrue();
        events.OfType<IndexEvent.FileScanned>().Should().ContainSingle();
    }

    [Fact]
    public async Task Import_free_document_emits_complete_versioned_roundtrippable_payload()
    {
        const string source = """
            syntax = "proto3";
            package sample.v1;
            message Request {
              optional string id = 1;
              repeated int32 values = 2;
            }
            """;
        var (path, contents) = Plant("src/request.proto", source);

        var events = await Index(path, contents);
        var annotations = events
            .OfType<IndexEvent.AnnotationAttached>()
            .ToArray();
        annotations.Should().HaveCount(3);
        foreach (var annotation in annotations)
        {
            annotation.ArgsJson.Should().StartWith(
                """{"version":1,"kind":""");
            var fact = ProtoContractPayloadCodec.Decode(annotation.ArgsJson!);
            fact.Status.Should().Be(ProtoContractStatus.Complete);
            fact.ImportCount.Should().Be(0);
            fact.IncompleteReasons.Should().BeEmpty();
            ProtoContractPayloadCodec.Decode(
                    ProtoContractPayloadCodec.Encode(fact))
                .Should().BeEquivalentTo(fact);
        }

        var fields = annotations
            .Select(annotation =>
                ProtoContractPayloadCodec.Decode(annotation.ArgsJson!))
            .Where(fact => fact.Kind == ProtoContractKind.Field)
            .OrderBy(fact => fact.Field!.Number)
            .ToArray();
        fields[0].Field!.Cardinality.Should()
            .Be(ProtoFieldCardinality.Optional);
        fields[1].Field!.Cardinality.Should()
            .Be(ProtoFieldCardinality.Repeated);
    }

    [Theory]
    [InlineData("unknown-version")]
    [InlineData("unknown-property")]
    [InlineData("duplicate-property")]
    public async Task Strict_payload_decoder_rejects_schema_drift(string mutation)
    {
        var (path, contents) = Plant(
            "src/payload.proto",
            """syntax = "proto3"; message Value { string text = 1; }""");
        var events = await Index(path, contents);
        var json = events
            .OfType<IndexEvent.AnnotationAttached>()
            .First()
            .ArgsJson!;
        var mutated = mutation switch
        {
            "unknown-version" => json.Replace(
                "\"version\":1",
                "\"version\":2",
                StringComparison.Ordinal),
            "unknown-property" => json[..^1] + ",\"unknown\":true}",
            "duplicate-property" => json.Replace(
                "\"version\":1",
                "\"version\":1,\"version\":1",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException("Unknown test mutation."),
        };

        var act = () => ProtoContractPayloadCodec.Decode(mutated);
        act.Should().Throw<ProtoContractPayloadException>();
    }

    [Fact]
    public async Task Strict_payload_decoder_rejects_invalid_or_mismatched_package()
    {
        var facts = await IdentityPayloads();
        var topLevelMessage = facts[
            "proto:M:identity.v1.Outer"];
        var rpc = facts[
            "proto:R:identity.v1.Api.Call"];
        var mutations = new[]
        {
            Tamper(
                topLevelMessage,
                "\"package\":\"identity.v1\"",
                "\"package\":\"identity..v1\""),
            Tamper(
                topLevelMessage,
                "\"package\":\"identity.v1\"",
                "\"package\":\"other.v1\""),
            Tamper(
                rpc,
                "\"package\":\"identity.v1\"",
                "\"package\":\"identity\""),
        };

        foreach (var mutated in mutations)
        {
            var act = () => ProtoContractPayloadCodec.Decode(mutated);
            act.Should().Throw<ProtoContractPayloadException>();
        }
    }

    [Fact]
    public async Task Strict_payload_decoder_rejects_tampered_parent_depth_and_owner()
    {
        var facts = await IdentityPayloads();
        var nested = facts[
            "proto:M:identity.v1.Outer.Inner"];
        var field = facts[
            "proto:F:identity.v1.Outer.Inner.value"];
        var rpc = facts[
            "proto:R:identity.v1.Api.Call"];
        var mutations = new[]
        {
            Tamper(
                nested,
                "\"parent_full_name\":\"identity.v1.Outer\"",
                "\"parent_full_name\":\"identity.v1.Other\""),
            Tamper(
                nested,
                "\"nesting_depth\":1",
                "\"nesting_depth\":2"),
            Tamper(
                field,
                "\"containing_message_full_name\":\"identity.v1.Outer.Inner\"",
                "\"containing_message_full_name\":\"other.v1.Outer.Inner\""),
            Tamper(
                rpc,
                "\"service_full_name\":\"identity.v1.Api\"",
                "\"service_full_name\":\"identity.v1.Container.Api\""),
        };

        foreach (var mutated in mutations)
        {
            var act = () => ProtoContractPayloadCodec.Decode(mutated);
            act.Should().Throw<ProtoContractPayloadException>();
        }
    }

    [Fact]
    public async Task Same_source_emits_deterministically_ordered_symbols_and_payloads()
    {
        const string source = """
            syntax = "proto3";
            package stable.v1;
            message Outer {
              int32 id = 1;
              message Inner { string value = 1; }
            }
            service Api {
              rpc Stream(stream Outer) returns (stream Outer.Inner);
            }
            """;
        var (path, contents) = Plant("src/stable.proto", source);
        var indexer = new ProtobufLanguageIndexer();
        var ctx = new IndexContext(path, contents, "test", _root);

        var first = await indexer.IndexAsync(ctx, CancellationToken.None);
        var second = await indexer.IndexAsync(ctx, CancellationToken.None);

        first.Select(Fingerprint).Should().Equal(second.Select(Fingerprint));
        first.OfType<IndexEvent.SymbolDeclared>()
            .Select(symbol => symbol.CanonicalKey)
            .Should().Equal(
                "proto:M:stable.v1.Outer",
                "proto:F:stable.v1.Outer.id",
                "proto:M:stable.v1.Outer.Inner",
                "proto:F:stable.v1.Outer.Inner.value",
                "proto:R:stable.v1.Api.Stream");
    }

    [Fact]
    public async Task Excluded_medical_path_is_rejected_before_invalid_bytes_are_decoded()
    {
        var path = Path.Join(_root, "PatientData", "secret.proto");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var invalidUtf8 = new byte[] { 0xff, 0xfe, 0xfd };
        await File.WriteAllBytesAsync(path, invalidUtf8);

        var events = await new ProtobufLanguageIndexer().IndexAsync(
            new IndexContext(path, invalidUtf8, "test", _root),
            CancellationToken.None);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task Configured_exclude_is_rejected_before_source_is_parsed()
    {
        var path = Path.Join(_root, "generated", "bad.proto");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var malformed = Encoding.UTF8.GetBytes("message Broken {");
        await File.WriteAllBytesAsync(path, malformed);

        var events = await new ProtobufLanguageIndexer().IndexAsync(
            new IndexContext(
                path,
                malformed,
                "test",
                _root,
                project: null,
                excludePatterns: ["generated/**"]),
            CancellationToken.None);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task Oversized_source_fails_with_a_bounded_structured_error()
    {
        var contents = Enumerable.Repeat(
                (byte)' ',
                ProtobufLanguageIndexer.MaximumSourceBytes + 1)
            .ToArray();
        var path = Path.Join(_root, "src", "large.proto");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, contents);
        var indexer = new ProtobufLanguageIndexer();

        Func<Task> act = async () => await indexer.IndexAsync(
            new IndexContext(path, contents, "test", _root),
            CancellationToken.None);

        var exception = await act.Should()
            .ThrowAsync<ProtobufSourceIndexingException>();
        exception.Which.Kind.Should()
            .Be(ProtobufSourceFailureKind.SourceTooLarge);
    }

    [Fact]
    public async Task Malicious_nesting_fails_at_the_parser_budget()
    {
        var source = new StringBuilder("""syntax = "proto3";""");
        for (var i = 0;
             i <= ProtobufLanguageIndexer.MaximumMessageNesting + 1;
             i++)
        {
            source.Append("message M").Append(i).Append('{');
        }
        for (var i = 0;
             i <= ProtobufLanguageIndexer.MaximumMessageNesting + 1;
             i++)
        {
            source.Append('}');
        }
        var (path, contents) = Plant("src/deep.proto", source.ToString());

        Func<Task> act = async () => await new ProtobufLanguageIndexer()
            .IndexAsync(
                new IndexContext(path, contents, "test", _root),
                CancellationToken.None);

        var exception = await act.Should()
            .ThrowAsync<ProtobufSourceIndexingException>();
        exception.Which.Kind.Should()
            .Be(ProtobufSourceFailureKind.LimitExceeded);
    }

    [Fact]
    public async Task Syntax_error_is_structured_and_does_not_emit_partial_declarations()
    {
        var (path, contents) = Plant(
            "src/broken.proto",
            """
            syntax = "proto3";
            message Broken {
              string value = 1;
            """);

        Func<Task> act = async () => await new ProtobufLanguageIndexer()
            .IndexAsync(
                new IndexContext(path, contents, "test", _root),
                CancellationToken.None);

        var exception = await act.Should()
            .ThrowAsync<ProtobufSourceIndexingException>();
        exception.Which.Kind.Should()
            .Be(ProtobufSourceFailureKind.SyntaxError);
        exception.Which.Line.Should().NotBeNull();
        exception.Which.Column.Should().NotBeNull();
    }

    [Fact]
    public async Task Dispatcher_replaces_retains_last_good_on_error_and_deletes_proto_facts()
    {
        var path = Path.Join(_root, "contracts", "api.proto");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """syntax = "proto3"; package demo; message Before {}""");

        var registry = new LanguageIndexerRegistry();
        registry.Register(new ProtobufLanguageIndexer());
        var dispatcher = new LanguageIndexerDispatcher(
            registry,
            new LanguageProjectFactoryRegistry());
        await using var store = new SqliteGraphStore(
            Path.Join(_root, "graph.db"));
        await store.EnsureSchemaAsync();

        var first = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            _root,
            new Dictionary<string, ILanguageProject>());
        first.IndexedFiles.Should().Be(1);
        (await store.GetAllSymbolKeysAsync()).Should().ContainSingle(row =>
            row.CanonicalKey == "proto:M:demo.Before");

        await File.WriteAllBytesAsync(
            path,
            new byte[ProtobufLanguageIndexer.MaximumSourceBytes + 1]);
        var oversized = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            _root,
            new Dictionary<string, ILanguageProject>());
        oversized.FailedFiles.Should().ContainSingle(failure =>
            failure.Reason.Contains(
                $"{ProtobufLanguageIndexer.MaximumSourceBytes}-byte limit",
                StringComparison.Ordinal));
        (await store.GetAllSymbolKeysAsync()).Should().ContainSingle(row =>
            row.CanonicalKey == "proto:M:demo.Before",
            "the bounded read fails before the replacement transaction");

        await File.WriteAllTextAsync(path, "message Broken {");
        var failed = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            _root,
            new Dictionary<string, ILanguageProject>());
        failed.FailedFiles.Should().ContainSingle();
        (await store.GetAllSymbolKeysAsync()).Should().ContainSingle(row =>
            row.CanonicalKey == "proto:M:demo.Before",
            "a parser failure happens before the replacement transaction");

        await File.WriteAllTextAsync(
            path,
            """syntax = "proto3"; package demo; message After {}""");
        var replaced = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            _root,
            new Dictionary<string, ILanguageProject>());
        replaced.IndexedFiles.Should().Be(1);
        (await store.GetAllSymbolKeysAsync()).Should().ContainSingle(row =>
            row.CanonicalKey == "proto:M:demo.After");

        File.Delete(path);
        var deleted = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            _root,
            new Dictionary<string, ILanguageProject>());
        deleted.DeletedFiles.Should().Be(1);
        (await store.GetAllFilesAsync()).Should().BeEmpty();
        (await store.GetAllSymbolKeysAsync()).Should().BeEmpty();
    }

    [Fact]
    public void Built_in_registry_claims_proto_once()
    {
        var registry = new LanguageIndexerRegistry();

        var results = BuiltInIndexers.RegisterAll(registry);

        results.SelectMany(result => result.RejectedExtensions)
            .Should().BeEmpty();
        var proto = registry.TryGet(".proto");
        proto.Should().NotBeNull();
        proto!.Value.Indexer.Should().BeOfType<ProtobufLanguageIndexer>();
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_parsing()
    {
        var (path, contents) = Plant(
            "src/cancelled.proto",
            """syntax = "proto3"; message Value {}""");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = async () => await new ProtobufLanguageIndexer()
            .IndexAsync(
                new IndexContext(path, contents, "test", _root),
                cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<IReadOnlyList<IndexEvent>> Index(
        string path,
        byte[] contents) =>
        await new ProtobufLanguageIndexer().IndexAsync(
            new IndexContext(path, contents, "test", _root),
            CancellationToken.None);

    private async Task<IReadOnlyDictionary<string, string>> IdentityPayloads()
    {
        const string source = """
            syntax = "proto3";
            package identity.v1;
            message Outer {
              message Inner { string value = 1; }
            }
            service Api {
              rpc Call(Outer.Inner) returns (Outer);
            }
            """;
        var (path, contents) = Plant("src/identity.proto", source);
        var events = await Index(path, contents);
        return events
            .OfType<IndexEvent.AnnotationAttached>()
            .ToDictionary(
                annotation => annotation.SymbolCanonicalKey,
                annotation => annotation.ArgsJson!,
                StringComparer.Ordinal);
    }

    private static string Tamper(
        string json,
        string original,
        string replacement)
    {
        json.Should().Contain(original);
        return json.Replace(
            original,
            replacement,
            StringComparison.Ordinal);
    }

    private (string Path, byte[] Contents) Plant(
        string relativePath,
        string source)
    {
        var path = Path.Join(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var contents = Encoding.UTF8.GetBytes(source);
        File.WriteAllBytes(path, contents);
        return (path, contents);
    }

    private static string Fingerprint(IndexEvent value) => value switch
    {
        IndexEvent.SymbolDeclared symbol =>
            "symbol|" + string.Join(
                "|",
                symbol.CanonicalKey,
                symbol.Name,
                symbol.Fqn,
                symbol.Kind,
                symbol.StartLine,
                symbol.StartColumn,
                symbol.EndLine,
                symbol.EndColumn,
                symbol.Signature,
                symbol.ContainerCanonicalKey,
                symbol.Modifiers),
        IndexEvent.AnnotationAttached annotation =>
            "annotation|" + string.Join(
                "|",
                annotation.SymbolCanonicalKey,
                annotation.AnnotationName,
                annotation.Flavor,
                annotation.FullName,
                annotation.ArgsJson),
        IndexEvent.FileScanned scanned =>
            "file|" + scanned.FilePath + "|"
            + Convert.ToHexString(scanned.ContentSha256.ToArray()),
        _ => value.ToString() ?? value.GetType().Name,
    };

    private static string LocateFixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Join(
                directory.FullName,
                "tests",
                "fixtures",
                name);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate tests/fixtures/{name}.");
    }
}
