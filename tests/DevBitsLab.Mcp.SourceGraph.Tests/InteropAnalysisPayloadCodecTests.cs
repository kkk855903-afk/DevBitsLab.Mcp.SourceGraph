using System.Text.Json;
using System.Text.Json.Nodes;
using DevBitsLab.Mcp.SourceGraph.Core;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class InteropAnalysisPayloadCodecTests
{
    [Fact]
    public void AnnotationFlavors_areStable()
    {
        InteropAnnotationFlavors.Match.Should().Be("interop-match");
        InteropAnnotationFlavors.Finding.Should().Be("interop-finding");
    }

    [Fact]
    public void Match_roundTripsWithCanonicalOwnerIndependentJson()
    {
        var first = CreateMatch(reverseMetadata: true);
        var second = CreateMatch(reverseMetadata: false);

        var json = InteropFactPayloadCodec.EncodeMatch(first);
        var canonicalJson = InteropFactPayloadCodec.EncodeMatch(second);
        var decoded = InteropFactPayloadCodec.DecodeMatch(json);

        json.Should().Be(canonicalJson);
        decoded.Should().BeEquivalentTo(second);
        json.Should().StartWith(
            "{\"version\":1,\"kind\":\"match\","
            + "\"managed_symbol_canonical_key\":");
        json.Should().Contain("\"status\":\"matched\"");
        json.Should().Contain("\"confidence\":\"exact\"");
        json.Should().Contain("\"candidate_count\":1");
        json.Should().Contain("\"snapshot_complete\":true");
        json.IndexOf("\"alpha\":\"first\"", StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                json.IndexOf("\"zeta\":\"last\"", StringComparison.Ordinal));
        json.Should().NotContain("producing_file_id");
        json.Should().NotContain("ProducingFileId");
        typeof(InteropEvidenceProjection)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain("ProducingFileId");
    }

    [Fact]
    public void Finding_roundTripsWithCanonicalEnumsAndExplicitTarget()
    {
        var finding = CreateFinding();

        var json = InteropFactPayloadCodec.EncodeFinding(finding);
        var decoded = InteropFactPayloadCodec.DecodeFinding(json);

        decoded.Should().BeEquivalentTo(finding);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("version").GetInt32().Should().Be(2);
        root.GetProperty("kind").GetString().Should().Be("finding");
        root.GetProperty("rule_id").GetString().Should().Be("Interop001");
        root.GetProperty("severity").GetString().Should().Be("error");
        root.GetProperty("confidence").GetString().Should().Be("exact");
        root.GetProperty("boundary_managed_symbol_canonical_key")
            .GetString()
            .Should()
            .Be(finding.ManagedSymbolCanonicalKey);
        root.GetProperty("target")
            .GetProperty("runtime_identifier")
            .GetString()
            .Should()
            .Be("win-x64");
        json.Should().NotContain("producing_file_id");
    }

    [Fact]
    public void Finding_v2_roundTripsDistinctCallerAndBoundaryKeys()
    {
        var finding = CreateFinding() with
        {
            ManagedSymbolCanonicalKey =
                "csharp:M:Example.Service.ReleaseNativeBuffer",
            BoundaryManagedSymbolCanonicalKey =
                "csharp:M:Example.NativeMethods.Run",
        };

        var json = InteropFactPayloadCodec.EncodeFinding(finding);
        var decoded = InteropFactPayloadCodec.DecodeFinding(json);

        decoded.ManagedSymbolCanonicalKey.Should().Be(
            "csharp:M:Example.Service.ReleaseNativeBuffer");
        decoded.BoundaryManagedSymbolCanonicalKey.Should().Be(
            "csharp:M:Example.NativeMethods.Run");
    }

    [Fact]
    public void Finding_v1_decodesBoundaryAsLegacyManagedOwner()
    {
        var v2 = InteropFactPayloadCodec.EncodeFinding(CreateFinding());
        var v1 = Mutate(
            v2,
            root =>
            {
                root["version"] = 1;
                root.Remove("boundary_managed_symbol_canonical_key");
            });

        var decoded = InteropFactPayloadCodec.DecodeFinding(v1);

        decoded.BoundaryManagedSymbolCanonicalKey.Should().Be(
            decoded.ManagedSymbolCanonicalKey);
    }

    [Fact]
    public void Finding_rejects_v2_boundary_field_disguised_as_v1()
    {
        var hybrid = InteropFactPayloadCodec.EncodeFinding(
                CreateFinding() with
                {
                    ManagedSymbolCanonicalKey =
                        "csharp:M:Example.Service.Release",
                    BoundaryManagedSymbolCanonicalKey =
                        "csharp:M:Example.NativeMethods.Run",
                })
            .Replace(
                "\"version\":2",
                "\"version\":1",
                StringComparison.Ordinal);

        var act = () => InteropFactPayloadCodec.DecodeFinding(hybrid);

        act.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*not valid*version 1*");
    }

    [Fact]
    public void Finding_rejects_null_v2_boundary_field_disguised_as_v1()
    {
        var hybrid = Mutate(
            InteropFactPayloadCodec.EncodeFinding(CreateFinding()),
            root =>
            {
                root["version"] = 1;
                root["boundary_managed_symbol_canonical_key"] = null;
            });

        var act = () => InteropFactPayloadCodec.DecodeFinding(hybrid);

        act.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*not valid*version 1*");
    }

    [Theory]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("status")]
    [InlineData("confidence")]
    public void Match_rejectsUnknownVersionKindAndEnumTokens(string mutation)
    {
        var json = InteropFactPayloadCodec.EncodeMatch(CreateMatch());
        json = mutation switch
        {
            "version" => json.Replace(
                "\"version\":1",
                "\"version\":2",
                StringComparison.Ordinal),
            "kind" => json.Replace(
                "\"kind\":\"match\"",
                "\"kind\":\"finding\"",
                StringComparison.Ordinal),
            "status" => json.Replace(
                "\"status\":\"matched\"",
                "\"status\":\"MATCHED\"",
                StringComparison.Ordinal),
            "confidence" => json.Replace(
                "\"confidence\":\"exact\"",
                "\"confidence\":\"EXACT\"",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };

        var act = () => InteropFactPayloadCodec.DecodeMatch(json);

        act.Should().Throw<InteropFactPayloadException>();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("severity")]
    [InlineData("confidence")]
    public void Finding_rejectsUnknownVersionKindAndEnumTokens(string mutation)
    {
        var json = InteropFactPayloadCodec.EncodeFinding(CreateFinding());
        json = mutation switch
        {
            "version" => json.Replace(
                "\"version\":2",
                "\"version\":0",
                StringComparison.Ordinal),
            "kind" => json.Replace(
                "\"kind\":\"finding\"",
                "\"kind\":\"match\"",
                StringComparison.Ordinal),
            "severity" => json.Replace(
                "\"severity\":\"error\"",
                "\"severity\":\"ERROR\"",
                StringComparison.Ordinal),
            "confidence" => json.Replace(
                "\"confidence\":\"exact\"",
                "\"confidence\":\"unknown\"",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };

        var act = () => InteropFactPayloadCodec.DecodeFinding(json);

        act.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Decode_rejectsUnknownRootAndNestedProperties()
    {
        var json = InteropFactPayloadCodec.EncodeMatch(CreateMatch());
        var rootUnknown = json.Replace(
            "\"version\":1",
            "\"version\":1,\"unexpected\":true",
            StringComparison.Ordinal);
        var nestedUnknown = json.Replace(
            "\"runtime_identifier\":\"win-x64\"",
            "\"runtime_identifier\":\"win-x64\",\"unexpected\":true",
            StringComparison.Ordinal);

        var rootAct = () => InteropFactPayloadCodec.DecodeMatch(rootUnknown);
        var nestedAct = () => InteropFactPayloadCodec.DecodeMatch(nestedUnknown);

        rootAct.Should().Throw<InteropFactPayloadException>();
        nestedAct.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Decode_rejectsDuplicateRootAndMetadataProperties()
    {
        var json = InteropFactPayloadCodec.EncodeFinding(CreateFinding());
        var rootDuplicate = json.Replace(
            "\"version\":2",
            "\"version\":2,\"version\":2",
            StringComparison.Ordinal);
        var metadataDuplicate = json.Replace(
            "\"alpha\":\"first\"",
            "\"alpha\":\"first\",\"alpha\":\"other\"",
            StringComparison.Ordinal);

        var rootAct = () => InteropFactPayloadCodec.DecodeFinding(rootDuplicate);
        var metadataAct = () =>
            InteropFactPayloadCodec.DecodeFinding(metadataDuplicate);

        rootAct.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*duplicate*");
        metadataAct.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*duplicate*");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong_type")]
    [InlineData("null_item")]
    public void Match_rejectsMissingWronglyTypedAndNullItems(string mutation)
    {
        var json = InteropFactPayloadCodec.EncodeMatch(CreateMatch());
        json = Mutate(
            json,
            root =>
            {
                switch (mutation)
                {
                    case "missing":
                        root.Remove("snapshot_complete");
                        break;
                    case "wrong_type":
                        root["candidate_count"] = "1";
                        break;
                    case "null_item":
                        root["evidence"]!.AsArray()[0] = null;
                        break;
                }
            });

        var act = () => InteropFactPayloadCodec.DecodeMatch(json);

        act.Should().Throw<InteropFactPayloadException>();
    }

    [Theory]
    [InlineData("negative_candidates")]
    [InlineData("empty_reasons")]
    [InlineData("empty_evidence")]
    [InlineData("matched_incomplete")]
    [InlineData("matched_without_native")]
    [InlineData("matched_without_candidates")]
    [InlineData("unmatched_incomplete")]
    [InlineData("unmatched_with_native")]
    [InlineData("unmatched_with_candidates")]
    [InlineData("ambiguous_with_native")]
    [InlineData("ambiguous_without_multiple_candidates")]
    [InlineData("unknown_with_native")]
    [InlineData("source_matched_incomplete")]
    [InlineData("source_matched_without_native")]
    [InlineData("source_matched_without_candidates")]
    public void Match_rejectsInvalidSemanticStates(string invalidCase)
    {
        var match = CreateMatch();
        match = invalidCase switch
        {
            "negative_candidates" => match with { CandidateCount = -1 },
            "empty_reasons" => match with { Reasons = [] },
            "empty_evidence" => match with { Evidence = [] },
            "matched_incomplete" => match with { SnapshotComplete = false },
            "matched_without_native" => match with
            {
                NativeSymbolCanonicalKey = null,
            },
            "matched_without_candidates" => match with { CandidateCount = 0 },
            "unmatched_incomplete" => match with
            {
                Status = InteropMatchStatus.Unmatched,
                NativeSymbolCanonicalKey = null,
                CandidateCount = 0,
                SnapshotComplete = false,
            },
            "unmatched_with_native" => match with
            {
                Status = InteropMatchStatus.Unmatched,
                CandidateCount = 0,
            },
            "unmatched_with_candidates" => match with
            {
                Status = InteropMatchStatus.Unmatched,
                NativeSymbolCanonicalKey = null,
                CandidateCount = 1,
            },
            "ambiguous_with_native" => match with
            {
                Status = InteropMatchStatus.Ambiguous,
                CandidateCount = 2,
            },
            "ambiguous_without_multiple_candidates" => match with
            {
                Status = InteropMatchStatus.Ambiguous,
                NativeSymbolCanonicalKey = null,
                CandidateCount = 1,
            },
            "unknown_with_native" => match with
            {
                Status = InteropMatchStatus.Unknown,
            },
            "source_matched_incomplete" => match with
            {
                Status = InteropMatchStatus.SourceMatched,
                SnapshotComplete = false,
            },
            "source_matched_without_native" => match with
            {
                Status = InteropMatchStatus.SourceMatched,
                NativeSymbolCanonicalKey = null,
            },
            "source_matched_without_candidates" => match with
            {
                Status = InteropMatchStatus.SourceMatched,
                CandidateCount = 0,
            },
            _ => throw new InvalidOperationException(),
        };

        var act = () => InteropFactPayloadCodec.EncodeMatch(match);

        act.Should().Throw<InteropFactPayloadException>();
    }

    [Theory]
    [InlineData("Interop000")]
    [InlineData("Interop01")]
    [InlineData("Interop1000")]
    [InlineData("interop001")]
    [InlineData("Interop00A")]
    [InlineData("Interop 01")]
    public void Finding_rejectsRuleIdsOutsideInterop001ThroughInterop999(
        string ruleId)
    {
        var finding = CreateFinding() with { RuleId = ruleId };

        var act = () => InteropFactPayloadCodec.EncodeFinding(finding);

        act.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*Interop001*Interop999*");
    }

    [Theory]
    [InlineData("message")]
    [InlineData("managed")]
    [InlineData("native")]
    [InlineData("evidence")]
    public void Finding_requiresMatchedBoundaryAndEvidence(string missing)
    {
        var finding = CreateFinding();
        finding = missing switch
        {
            "message" => finding with { Message = "" },
            "managed" => finding with { ManagedSymbolCanonicalKey = "" },
            "native" => finding with { NativeSymbolCanonicalKey = null! },
            "evidence" => finding with { Evidence = [] },
            _ => throw new InvalidOperationException(),
        };

        var act = () => InteropFactPayloadCodec.EncodeFinding(finding);

        act.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Encoding_rejectsUnknownEnumValues()
    {
        var invalidMatch = CreateMatch() with
        {
            Status = (InteropMatchStatus)999,
        };
        var invalidFinding = CreateFinding() with
        {
            Severity = (InteropFindingSeverity)999,
        };

        var matchAct = () => InteropFactPayloadCodec.EncodeMatch(invalidMatch);
        var findingAct = () =>
            InteropFactPayloadCodec.EncodeFinding(invalidFinding);

        matchAct.Should().Throw<InteropFactPayloadException>();
        findingAct.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Match_rejectsOversizedCollectionsStringsAndMetadata()
    {
        var tooManyReasons = Enumerable.Repeat("reason", 4097).ToArray();
        var oversizedReason = new string('x', 32 * 1024 + 1);
        var tooMuchMetadata = Enumerable.Range(0, 257).ToDictionary(
            index => $"key-{index}",
            index => $"value-{index}",
            StringComparer.Ordinal);

        var reasonsAct = () => InteropFactPayloadCodec.EncodeMatch(
            CreateMatch() with { Reasons = tooManyReasons });
        var stringAct = () => InteropFactPayloadCodec.EncodeMatch(
            CreateMatch() with { Reasons = [oversizedReason] });
        var metadataAct = () => InteropFactPayloadCodec.EncodeMatch(
            CreateMatch() with
            {
                Evidence =
                [
                    CreateEvidence(metadata: tooMuchMetadata),
                ],
            });

        reasonsAct.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*item limit*");
        stringAct.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*character limit*");
        metadataAct.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*entry limit*");
    }

    [Fact]
    public void Decode_rejectsPayloadBeyondByteLimit()
    {
        var json = new string(
            'x',
            InteropFactPayloadCodec.MaximumPayloadBytes + 1);

        var matchAct = () => InteropFactPayloadCodec.DecodeMatch(json);
        var findingAct = () => InteropFactPayloadCodec.DecodeFinding(json);

        matchAct.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*byte limit*");
        findingAct.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*byte limit*");
    }

    [Fact]
    public void Decode_rejectsCrossFlavorPayloads()
    {
        var matchJson = InteropFactPayloadCodec.EncodeMatch(CreateMatch());
        var findingJson = InteropFactPayloadCodec.EncodeFinding(CreateFinding());

        var matchAct = () => InteropFactPayloadCodec.DecodeMatch(findingJson);
        var findingAct = () =>
            InteropFactPayloadCodec.DecodeFinding(matchJson);

        matchAct.Should().Throw<InteropFactPayloadException>();
        findingAct.Should().Throw<InteropFactPayloadException>();
    }

    private static InteropMatchProjection CreateMatch(
        bool reverseMetadata = false) =>
        new(
            "csharp:M:Example.NativeMethods.Run",
            "cpp:function:run",
            InteropMatchStatus.Matched,
            EvidenceConfidence.Exact,
            ["exact binary export name"],
            InteropTarget.WindowsX64Msvc,
            CandidateCount: 1,
            SnapshotComplete: true,
            [CreateEvidence(reverseMetadata)]);

    private static InteropFindingProjection CreateFinding() =>
        new(
            "Interop001",
            InteropFindingSeverity.Error,
            "The calling conventions differ.",
            "csharp:M:Example.NativeMethods.Run",
            "cpp:function:run",
            InteropTarget.WindowsX64Msvc,
            EvidenceConfidence.Exact,
            [CreateEvidence()]);

    private static InteropEvidenceProjection CreateEvidence(
        bool reverseMetadata = false,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (metadata is null)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (reverseMetadata)
            {
                values.Add("zeta", "last");
                values.Add("alpha", "first");
            }
            else
            {
                values.Add("alpha", "first");
                values.Add("zeta", "last");
            }
            metadata = values;
        }

        return new InteropEvidenceProjection(
            new SourceLocation("native.h", 4, 2, 4, 9),
            EvidenceConfidence.Exact,
            "interop-match",
            metadata);
    }

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }
}
