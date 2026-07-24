using DevBitsLab.Mcp.SourceGraph.Core;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ManagedInteropUsagePayloadCodecTests
{
    [Fact]
    public void Annotation_flavors_are_stable()
    {
        InteropAnnotationFlavors.ManagedCallbackUsage.Should().Be(
            "interop-managed-callback-usage");
        InteropAnnotationFlavors.ManagedReturnRelease.Should().Be(
            "interop-managed-return-release");
    }

    [Fact]
    public void Callback_usage_round_trips_and_rebinds_evidence_owner()
    {
        var projection = Callback(ownerFileId: 91);

        var json =
            InteropFactPayloadCodec.EncodeManagedCallbackUsage(projection);
        var decoded = InteropFactPayloadCodec.DecodeManagedCallbackUsage(
            json,
            ownerFileId: 42);

        decoded.Should().BeEquivalentTo(Callback(ownerFileId: 42));
        json.Should().StartWith(
            "{\"version\":1,\"kind\":\"managed_callback_usage\",");
        json.Should().NotContain("producing_file_id");
    }

    [Fact]
    public void Return_release_round_trips_and_rebinds_evidence_owner()
    {
        var projection = Release(ownerFileId: 91);

        var json =
            InteropFactPayloadCodec.EncodeManagedReturnRelease(projection);
        var decoded = InteropFactPayloadCodec.DecodeManagedReturnRelease(
            json,
            ownerFileId: 42);

        decoded.Should().BeEquivalentTo(Release(ownerFileId: 42));
        json.Should().StartWith(
            "{\"version\":1,\"kind\":\"managed_return_release\",");
        json.Should().NotContain("producing_file_id");
    }

    [Fact]
    public void Callback_usage_rejects_unknown_rooting_and_negative_position()
    {
        var json = InteropFactPayloadCodec.EncodeManagedCallbackUsage(
            Callback(ownerFileId: 1));
        var unknown = json.Replace(
            "\"rooting\":\"unrooted\"",
            "\"rooting\":\"unknown\"",
            StringComparison.Ordinal);
        var negative = json.Replace(
            "\"parameter_position\":0",
            "\"parameter_position\":-1",
            StringComparison.Ordinal);

        var unknownAct = () =>
            InteropFactPayloadCodec.DecodeManagedCallbackUsage(unknown, 1);
        var negativeAct = () =>
            InteropFactPayloadCodec.DecodeManagedCallbackUsage(negative, 1);

        unknownAct.Should().Throw<InteropFactPayloadException>();
        negativeAct.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Return_release_rejects_unknown_allocator_and_cross_flavor_payload()
    {
        var releaseJson =
            InteropFactPayloadCodec.EncodeManagedReturnRelease(Release(1));
        var unknown = releaseJson.Replace(
            "\"release_family\":\"co_task_mem\"",
            "\"release_family\":\"unknown\"",
            StringComparison.Ordinal);
        var callbackJson =
            InteropFactPayloadCodec.EncodeManagedCallbackUsage(Callback(1));

        var unknownAct = () =>
            InteropFactPayloadCodec.DecodeManagedReturnRelease(unknown, 1);
        var crossFlavorAct = () =>
            InteropFactPayloadCodec.DecodeManagedReturnRelease(
                callbackJson,
                1);

        unknownAct.Should().Throw<InteropFactPayloadException>();
        crossFlavorAct.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Usage_payloads_require_positive_annotation_owner()
    {
        var callbackJson =
            InteropFactPayloadCodec.EncodeManagedCallbackUsage(Callback(1));
        var releaseJson =
            InteropFactPayloadCodec.EncodeManagedReturnRelease(Release(1));

        var callbackAct = () =>
            InteropFactPayloadCodec.DecodeManagedCallbackUsage(
                callbackJson,
                0);
        var releaseAct = () =>
            InteropFactPayloadCodec.DecodeManagedReturnRelease(
                releaseJson,
                -1);

        callbackAct.Should().Throw<ArgumentOutOfRangeException>();
        releaseAct.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static ManagedCallbackUsageProjection Callback(long ownerFileId) =>
        new(
            "csharp:M:Fixture.NativeMethods.Register",
            new ManagedCallbackUsage(
                ParameterPosition: 0,
                CallerSymbolCanonicalKey:
                    "csharp:M:Fixture.Service.RegisterCallback",
                CallbackGcRooting.Unrooted,
                InteropTarget.WindowsX64Msvc,
                EvidenceAt(ownerFileId, line: 12)));

    private static ManagedReturnReleaseProjection Release(long ownerFileId) =>
        new(
            "csharp:M:Fixture.NativeMethods.Allocate",
            new ManagedReturnRelease(
                "csharp:M:Fixture.Service.ReleaseBuffer",
                InteropAllocatorFamily.CoTaskMem,
                InteropTarget.WindowsX64Msvc,
                EvidenceAt(ownerFileId, line: 24)));

    private static Evidence EvidenceAt(long ownerFileId, int line) =>
        new(
            ownerFileId,
            new SourceLocation("Caller.cs", line, 5, line, 25),
            EvidenceConfidence.Semantic,
            "roslyn-managed-interop-usage",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["proof"] = "direct-operation",
            });
}
