using System.Text;
using DevBitsLab.Mcp.SourceGraph.Indexing.Cpp;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class CppSyntaxLanguageIndexerTests
{
    [Fact]
    public async Task Indexes_cpp_implementation_without_compiler_or_system_headers()
    {
        var events = await IndexAsync(
            """
            #include <mfapi.h>
            #include <vector>

            namespace camera
            {
                class CameraCapture final
                {
                public:
                    void StoreSample(int sample) { samples_.push_back(sample); }
                private:
                    std::vector<int> samples_;
                };

                int FindClosestNativeFormat(int requested)
                {
                    return requested;
                }
            }

            extern "C" int pg_camera_start(int requested)
            {
                return camera::FindClosestNativeFormat(requested);
            }
            """);

        var symbols = events.OfType<IndexEvent.SymbolDeclared>().ToArray();
        symbols.Should().Contain(symbol =>
            symbol.Name == "CameraCapture"
            && symbol.Kind == SymbolKinds.Class);
        symbols.Should().Contain(symbol =>
            symbol.Name == "StoreSample"
            && symbol.Kind == SymbolKinds.Method);
        symbols.Should().Contain(symbol =>
            symbol.Name == "FindClosestNativeFormat"
            && symbol.Kind == SymbolKinds.Function);
        symbols.Should().Contain(symbol =>
            symbol.Name == "pg_camera_start"
            && symbol.Kind == SymbolKinds.Function);
        symbols.Should().OnlyContain(symbol =>
            symbol.Modifiers == "syntax-only");
    }

    [Fact]
    public async Task Emits_intra_file_calls_with_position_evidence()
    {
        var events = await IndexAsync(
            """
            int normalize(int value) { return value; }
            int run(int value) { return normalize(value); }
            """);

        var symbols = events.OfType<IndexEvent.SymbolDeclared>().ToArray();
        var normalize = symbols.Single(symbol => symbol.Name == "normalize");
        var run = symbols.Single(symbol => symbol.Name == "run");
        events.OfType<IndexEvent.ReferenceFound>().Should().ContainSingle(reference =>
            reference.TargetCanonicalKey == normalize.CanonicalKey
            && reference.Kind == "call");
        var call = events.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle().Subject;
        call.SourceCanonicalKey.Should().Be(run.CanonicalKey);
        call.TargetCanonicalKey.Should().Be(normalize.CanonicalKey);
        call.EdgeKindName.Should().Be(EdgeKinds.Calls);
        call.Evidence.Should().NotBeNull();
        call.Evidence!.Producer.Should().Be("tree-sitter-cpp");
        call.Evidence.Confidence.Should().Be(EvidenceConfidence.Inferred);
    }

    [Fact]
    public async Task Anonymous_namespace_does_not_capture_a_descendant_type_as_its_name()
    {
        var events = await IndexAsync(
            """
            namespace
            {
                using DWORD = unsigned long;
                class CameraCapture {};
            }
            """);

        events.OfType<IndexEvent.SymbolDeclared>()
            .Single(symbol => symbol.Name == "CameraCapture")
            .Fqn.Should().Be("CameraCapture");
    }

    [Fact]
    public async Task Uses_c_grammar_and_canonical_scheme_for_c_sources()
    {
        var indexer = new CppSyntaxLanguageIndexer();
        var ctx = new IndexContext(
            "/repo/native/camera.c",
            Encoding.UTF8.GetBytes("int camera_start(void) { return 0; }"),
            "test",
            "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);

        events.OfType<IndexEvent.SymbolDeclared>()
            .Should().ContainSingle(symbol => symbol.Name == "camera_start")
            .Which.CanonicalKey.Should().StartWith("c:F:");
    }

    [Fact]
    public async Task Recovers_stable_declarations_when_compiler_extension_has_parse_error()
    {
        var events = await IndexAsync(
            """
            __declspec(dllexport) int exported(int value) { return value; }
            int after_extension(int value) { return exported(value); }
            @unsupported-token@
            """);

        events.OfType<IndexEvent.SymbolDeclared>().Should().Contain(symbol =>
            symbol.Name == "after_extension"
            && symbol.Modifiers == "syntax-only");
    }

    [Fact]
    public async Task Does_not_treat_local_object_construction_as_a_function()
    {
        var events = await IndexAsync(
            """
            class CameraCapture
            {
            public:
                void Start()
                {
                    std::scoped_lock lock(mutex_);
                    Sample sample(42);
                }
            };
            """);

        events.OfType<IndexEvent.SymbolDeclared>()
            .Should().NotContain(symbol =>
                symbol.Name == "lock" || symbol.Name == "sample");
    }

    [Fact]
    public async Task Type_signatures_do_not_include_the_entire_definition_body()
    {
        var events = await IndexAsync(
            """
            class CameraCapture final
            {
            public:
                void Start() {}
                void Stop() {}
            };
            """);

        var cameraCapture = events.OfType<IndexEvent.SymbolDeclared>()
            .Single(symbol => symbol.Name == "CameraCapture");
        cameraCapture.Signature.Should().Be("class CameraCapture");
    }

    [Fact]
    public void Claims_common_c_and_cpp_source_and_header_extensions()
    {
        var indexer = new CppSyntaxLanguageIndexer();

        indexer.FileExtensions.Should().Contain(
            [".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".inl"]);
    }

    private static async Task<IReadOnlyList<IndexEvent>> IndexAsync(
        string source)
    {
        var indexer = new CppSyntaxLanguageIndexer();
        var ctx = new IndexContext(
            "/repo/native/camera.cpp",
            Encoding.UTF8.GetBytes(source),
            "test",
            "/repo");
        return await indexer.IndexAsync(ctx, CancellationToken.None);
    }
}
