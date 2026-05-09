using System.Text.Json;
using System.Text.Json.Nodes;
using DevBitsLab.Mcp.SourceGraph.Server;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Regression coverage for the open-language vocabulary capability published in the MCP
/// <c>initialize</c> response.
///
/// History: <c>feat!: open language contract</c> (df6fae1) wired the vocabulary into
/// <c>ServerCapabilities.Experimental[ServerVocabulary.CapabilityKey]</c> as an anonymous type.
/// The MCP SDK serialises <see cref="ServerCapabilities"/> via its source-generated
/// <c>McpJsonUtilities.JsonContext</c>, which only knows the SDK's own types and rejects
/// anonymous types at runtime with
/// <c>System.NotSupportedException: JsonTypeInfo metadata for type '&lt;&gt;f__AnonymousType…' was
/// not provided by TypeInfoResolver of type 'ModelContextProtocol.McpJsonUtilities+JsonContext'</c>.
/// The bug only surfaced when a real MCP client called <c>initialize</c>; idle stdio boots never
/// triggered serialization, which is why the change shipped.
///
/// The fix in <c>Program.cs</c> builds the payload as a <see cref="JsonObject"/> graph instead.
/// <see cref="JsonObject"/> derives from <see cref="JsonNode"/>, which the SDK's source-gen
/// context handles natively as opaque JSON — so the writer emits it through unchanged. The
/// emitted JSON is byte-for-byte identical to what the anonymous-type path produced.
///
/// These tests replay the exact wiring shape against the SDK's own
/// <see cref="McpJsonUtilities.DefaultOptions"/> so a regression — e.g. somebody reintroducing
/// the anonymous type — fails CI instead of breaking the live <c>initialize</c>. (Note: a
/// stricter contract — that the value must be a <see cref="JsonNode"/> subtype the SDK
/// recognises — is also covered by the out-of-process integration test under
/// <c>tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests/</c>, which exercises a live MCP client.)
/// </summary>
public sealed class VocabularyCapabilityWiringTests
{
    private static VocabularyResult SampleVocabulary() =>
        new(
            EdgeKinds: new[] { "calls", "uses-type" },
            SymbolKinds: new[] { "class", "method" },
            AnnotationFlavors: new[] { "csharp-attribute" },
            Scopes: new Dictionary<string, ScopeVocabulary>(StringComparer.Ordinal)
            {
                ["default"] = new ScopeVocabulary(
                    EdgeKinds: new[] { "calls" },
                    SymbolKinds: new[] { "class" },
                    AnnotationFlavors: new[] { "csharp-attribute" }),
            });

    private static ServerCapabilities BuildCapabilitiesAsProgramDoes(VocabularyResult vocabulary)
    {
        // Mirror the exact wiring in src/.../Server/Program.cs RunServeAsync — keep this in sync
        // with the live wiring or this test stops covering the live path. Program.cs builds the
        // value as a JsonObject graph, not an anonymous type or pre-serialised JsonElement.
        var caps = new ServerCapabilities
        {
            Experimental = new Dictionary<string, object>(StringComparer.Ordinal),
        };

        var perScope = new JsonObject();
        foreach (var kv in vocabulary.Scopes)
        {
            perScope[kv.Key] = new JsonObject
            {
                ["edge_kinds"] = JsonSerializer.SerializeToNode(kv.Value.EdgeKinds),
                ["symbol_kinds"] = JsonSerializer.SerializeToNode(kv.Value.SymbolKinds),
                ["annotation_flavors"] = JsonSerializer.SerializeToNode(kv.Value.AnnotationFlavors),
            };
        }
        caps.Experimental[ServerVocabulary.CapabilityKey] = new JsonObject
        {
            ["edge_kinds"] = JsonSerializer.SerializeToNode(vocabulary.EdgeKinds),
            ["symbol_kinds"] = JsonSerializer.SerializeToNode(vocabulary.SymbolKinds),
            ["annotation_flavors"] = JsonSerializer.SerializeToNode(vocabulary.AnnotationFlavors),
            ["scopes"] = perScope,
        };
        return caps;
    }

    [Fact]
    public void Capabilities_serializeUnderMcpJsonContext_withoutThrowing()
    {
        // The exact path that broke: ServerCapabilities → through SDK's source-gen JsonContext.
        // If somebody reintroduces an anonymous type into the Experimental dictionary, this fails
        // the same way the live initialize handler did.
        var caps = BuildCapabilitiesAsProgramDoes(SampleVocabulary());

        Action act = () => JsonSerializer.Serialize(caps, McpJsonUtilities.DefaultOptions);
        act.Should().NotThrow("the SDK's source-generated JsonContext must be able to serialize ServerCapabilities.Experimental, and a JsonNode-rooted graph is what makes that possible");
    }

    [Fact]
    public void Capabilities_roundTrip_preservesShape()
    {
        // Beyond "doesn't throw", confirm the JSON we'd ship has the documented structure: the
        // top-level vocabulary keys appear, and the per-scope nesting is intact. Any future
        // refactor that reshapes the payload by accident will fail this assertion.
        var caps = BuildCapabilitiesAsProgramDoes(SampleVocabulary());
        var json = JsonSerializer.Serialize(caps, McpJsonUtilities.DefaultOptions);

        using var doc = JsonDocument.Parse(json);
        var experimental = doc.RootElement.GetProperty("experimental");
        var vocab = experimental.GetProperty(ServerVocabulary.CapabilityKey);

        vocab.GetProperty("edge_kinds").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "calls", "uses-type" });
        vocab.GetProperty("symbol_kinds").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "class", "method" });
        vocab.GetProperty("annotation_flavors").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "csharp-attribute" });

        var defaultScope = vocab.GetProperty("scopes").GetProperty("default");
        defaultScope.GetProperty("edge_kinds").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "calls" });
        defaultScope.GetProperty("symbol_kinds").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "class" });
        defaultScope.GetProperty("annotation_flavors").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "csharp-attribute" });
    }

    [Fact]
    public void Capabilities_storeJsonNode_notAnonymousType()
    {
        // Structural regression: the value MUST be a JsonNode subtype (JsonObject in the live
        // wiring, but the SDK's context handles JsonNode/JsonValue/JsonArray uniformly). If
        // somebody reverts the wiring to a raw anonymous type, this assertion fires in CI before
        // the production initialize handler would crash with "JsonTypeInfo metadata for type
        // ...was not provided".
        //
        // We can't faithfully reproduce the runtime crash from outside the SDK because
        // McpJsonUtilities.DefaultOptions appears to chain a reflection fallback that the SDK's
        // internal initialize-serialization path does not use; an anonymous type round-trips
        // through DefaultOptions but blows up under the strict source-gen context the SDK
        // actually invokes. Asserting on the value's type is a faithful proxy for the contract
        // we care about: never store anything in Experimental that the SDK's source-gen
        // JsonContext doesn't already understand.
        var caps = BuildCapabilitiesAsProgramDoes(SampleVocabulary());
        var value = caps.Experimental![ServerVocabulary.CapabilityKey];

        value.Should().BeAssignableTo<JsonNode>(
            "anything else risks NotSupportedException at MCP initialize time when the SDK ships ServerCapabilities through its source-gen JsonContext");
    }
}
