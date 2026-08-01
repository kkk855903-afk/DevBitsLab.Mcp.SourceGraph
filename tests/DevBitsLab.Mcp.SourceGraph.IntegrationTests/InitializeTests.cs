using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.IntegrationTests;

/// <summary>
/// End-to-end coverage of the MCP <c>initialize</c> handshake against a freshly-spawned
/// sourcegraph server. Exercises both the SDK's serialisation of
/// <c>Capabilities.Experimental["sourcegraph.vocabulary"]</c> and the
/// <c>--no-instructions</c> / <c>SOURCEGRAPH_NO_INSTRUCTIONS</c> suppression knobs — paths the
/// in-process <c>ServerInstructionsWiringTests</c> deliberately skip because they read
/// <c>IOptions&lt;McpServerOptions&gt;</c> directly without round-tripping through the
/// transport.
/// </summary>
public sealed class InitializeTests
{
    /// <summary>
    /// Common args the integration suite passes to every <c>serve</c> spawn: pin embeddings off
    /// (no ONNX model download in CI) and history off (no git invocation in containerised CI),
    /// while still exercising the vocabulary publication path which is independent of both.
    /// </summary>
    private static readonly string[] CommonServeArgs =
    [
        "--no-history",
    ];

    private static string[] WithSolution(string solutionAbsolutePath, params string[] extra)
    {
        var args = new List<string>(CommonServeArgs.Length + 2 + extra.Length)
        {
            "--solution",
            solutionAbsolutePath,
        };
        args.AddRange(CommonServeArgs);
        args.AddRange(extra);
        return args.ToArray();
    }

    // §12.1 unblocked: RunServeAsync now caps min-level to Warning so info-level lifecycle
    // chatter (Microsoft.Hosting.Lifetime, StdioServerTransport) stays off stderr during the
    // initialize handshake. Plugin-host startup uses StartupLogging which inherits the same cap.
    [Fact]
    public async Task Initialize_and_tools_list_against_Sample_complete_and_exit_cleanly()
    {
        var sln = ServerHarness.LocateFixture("Sample.sln");
        await using var harness = await ServerHarness.StartAsync(WithSolution(sln));

        // The McpClient is already initialised by CreateAsync — we just assert the response we
        // got back is non-null and well-shaped. The harness's outer 10-second cap means a server
        // that never replied would have surfaced a TimeoutException long before we get here.
        harness.Client.ServerCapabilities.Should().NotBeNull();
        harness.Client.ServerInfo.Should().NotBeNull();

        // ListToolsAsync performs a real MCP `tools/list` request over the same stdio channel.
        // The pack job reruns this test with ServerHarness's command override pointed at the
        // just-installed package executable, so this assertion cannot be satisfied by the
        // in-repository build or by merely starting an idle child process.
        var tools = await harness.Client.ListToolsAsync();
        tools.Should().NotBeEmpty();
        tools.Should().Contain(tool => tool.Name == "ping");

        var completion = await harness.StopAsync();
        completion.Should().BeOfType<ModelContextProtocol.Client.StdioClientCompletionDetails>();
        completion.Exception.Should().BeNull(
            "the official MCP SDK defines a null completion exception as graceful stdio closure");

        // The server's Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
        // means stderr is silent at default verbosity. If something is going wrong (workspace
        // load failure, scope-config error, MSBuild noise) it shows up on stderr. We tolerate
        // common-noise prefixes ("info:", "warn:") but the handshake should produce nothing.
        var stderr = harness.StderrLines.ToArray();
        stderr.Should().BeEmpty(
            "the MCP `initialize` handshake should not log to stderr at default verbosity; " +
            $"saw: {string.Join(Environment.NewLine, stderr)}");
    }

    [Fact]
    public async Task Vocabulary_capability_carries_sorted_lowercase_kebab_arrays()
    {
        var sln = ServerHarness.LocateFixture("Sample.sln");
        await using var harness = await ServerHarness.StartAsync(WithSolution(sln));

        var capabilities = harness.Client.ServerCapabilities;
        capabilities.Should().NotBeNull();
        capabilities!.Experimental.Should().NotBeNull(
            "the server publishes its vocabulary under capabilities.experimental[\"sourcegraph.vocabulary\"]");
        capabilities.Experimental.Should().ContainKey(
            "sourcegraph.vocabulary",
            "the SDK round-trips Configure<McpServerOptions>() Experimental into the wire");

        // The SDK stores Experimental as IDictionary<string, object>. At the wire boundary the
        // value comes through as a JsonElement (System.Text.Json deserialises non-strongly-typed
        // dictionary values as JsonElement). We cast here so the test fails clearly if a future
        // SDK change boxes a different type.
        var raw = capabilities.Experimental!["sourcegraph.vocabulary"];
        raw.Should().BeOfType<JsonElement>("Experimental values come back as JsonElement at the wire");
        var vocab = (JsonElement)raw;

        var edgeKinds = ReadStringArray(vocab, "edge_kinds");
        var symbolKinds = ReadStringArray(vocab, "symbol_kinds");
        var annotationFlavors = ReadStringArray(vocab, "annotation_flavors");

        edgeKinds.Should().NotBeEmpty();
        symbolKinds.Should().NotBeEmpty();
        annotationFlavors.Should().NotBeEmpty();

        AssertSortedLowercaseDeduped(edgeKinds, nameof(edgeKinds));
        AssertSortedLowercaseDeduped(symbolKinds, nameof(symbolKinds));
        AssertSortedLowercaseDeduped(annotationFlavors, nameof(annotationFlavors));

        // Every constant string declared on the SDK's EdgeKinds must show up in the published
        // edge_kinds array. We pull the SDK constants by reflection so this test stays in lock-step
        // with the SDK without a hand-maintained echo of the kind list.
        var sdkEdgeKinds = ReadConstantStrings(SdkAssembly, "DevBitsLab.Mcp.SourceGraph.Sdk.EdgeKinds");
        sdkEdgeKinds.Should().NotBeEmpty("EdgeKinds should declare at least one constant");
        edgeKinds.Should().Contain(
            sdkEdgeKinds,
            "every SDK-declared edge kind must appear in the server's published vocabulary");
    }

    [Fact]
    public async Task NoInstructions_flag_suppresses_vocabulary_capability()
    {
        var sln = ServerHarness.LocateFixture("Sample.sln");
        await using var harness = await ServerHarness.StartAsync(
            WithSolution(sln, "--no-instructions"));

        // Match the existing ServerInstructions suppression behaviour in Program.cs: under
        // --no-instructions the server doesn't add the experimental key at all (the Capabilities
        // dictionary may be unset or simply not contain "sourcegraph.vocabulary"). Either is a
        // valid suppression — we assert the key is absent in both cases.
        var experimental = harness.Client.ServerCapabilities?.Experimental;
        if (experimental is not null)
        {
            experimental.Should().NotContainKey(
                "sourcegraph.vocabulary",
                "--no-instructions must suppress the experimental vocabulary key");
        }

        // The matching ServerInstructions string should also be suppressed — mirrors the
        // ServerInstructionsWiringTests assertions, but at the wire level.
        harness.Client.ServerInstructions.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task NoInstructions_envvar_suppresses_vocabulary_capability()
    {
        var sln = ServerHarness.LocateFixture("Sample.sln");
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SOURCEGRAPH_NO_INSTRUCTIONS"] = "1",
        };
        await using var harness = await ServerHarness.StartAsync(WithSolution(sln), env);

        var experimental = harness.Client.ServerCapabilities?.Experimental;
        if (experimental is not null)
        {
            experimental.Should().NotContainKey(
                "sourcegraph.vocabulary",
                "SOURCEGRAPH_NO_INSTRUCTIONS=1 must suppress the experimental vocabulary key");
        }
        harness.Client.ServerInstructions.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// Decode a string array out of the JSON object that came back as the value of one
    /// experimental capability key. The SDK exposes it as <see cref="JsonElement"/>; we use
    /// <c>EnumerateArray</c> instead of <c>Deserialize&lt;string[]&gt;</c> so a malformed value
    /// (object instead of array, missing key) produces a precise xUnit assertion failure rather
    /// than a generic JsonException.
    /// </summary>
    private static IReadOnlyList<string> ReadStringArray(JsonElement vocab, string propertyName)
    {
        vocab.ValueKind.Should().Be(JsonValueKind.Object,
            $"sourcegraph.vocabulary must be a JSON object; saw {vocab.ValueKind}");
        vocab.TryGetProperty(propertyName, out var arrayProp).Should().BeTrue(
            $"sourcegraph.vocabulary.{propertyName} must be present");
        arrayProp.ValueKind.Should().Be(JsonValueKind.Array,
            $"sourcegraph.vocabulary.{propertyName} must be a JSON array; saw {arrayProp.ValueKind}");

        var values = new List<string>();
        foreach (var item in arrayProp.EnumerateArray())
        {
            item.ValueKind.Should().Be(JsonValueKind.String,
                $"sourcegraph.vocabulary.{propertyName} entries must be strings");
            values.Add(item.GetString()!);
        }
        return values;
    }

    private static void AssertSortedLowercaseDeduped(IReadOnlyList<string> arr, string label)
    {
        arr.Should().Equal(arr.OrderBy(s => s, StringComparer.Ordinal),
            $"{label} must be sorted in ordinal order");
        arr.Should().OnlyContain(s => s == s.ToLowerInvariant(),
            $"{label} entries must be lowercase");
        arr.Should().OnlyHaveUniqueItems($"{label} must be deduplicated");
        // Kebab-case sanity: only [a-z0-9-]+, no leading/trailing dash, no double dash. The SDK's
        // KebabCaseValidator carries the canonical regex; we mirror the surface expectation here.
        // Loop manually instead of `.OnlyContain(s => …)`: FluentAssertions's predicate path
        // compiles the lambda as an expression tree, which doesn't support `s[^1]`.
        foreach (var s in arr)
        {
            IsKebabCase(s).Should().BeTrue($"{label} entry `{s}` must be kebab-case");
        }
    }

    private static bool IsKebabCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s[0] == '-' || s[s.Length - 1] == '-') return false;
        if (s.Contains("--", StringComparison.Ordinal)) return false;
        return s.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');
    }

    /// <summary>
    /// Reflection: enumerate every <c>public const string</c> field declared on the type named
    /// <paramref name="typeFullName"/> in <paramref name="assembly"/>. Used to pull the SDK's
    /// well-known kind list without taking a direct dependency on the SDK assembly from this
    /// project (we avoid the dependency so the integration suite never accidentally links the
    /// in-process types and silently skips the wire round-trip).
    /// </summary>
    private static IReadOnlyList<string> ReadConstantStrings(Assembly assembly, string typeFullName)
    {
        var t = assembly.GetType(typeFullName);
        t.Should().NotBeNull($"type {typeFullName} should exist in {assembly.GetName().Name}");
        return t!
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s.ToLowerInvariant())
            .ToList();
    }

    /// <summary>
    /// The SDK assembly, located by walking the same loaded-assembly directory. We can't
    /// <c>typeof(EdgeKinds)</c> here because the integration tests project doesn't reference
    /// the SDK directly; instead we walk to the Server's bin output where the SDK DLL is
    /// transitively copied. This keeps the integration suite an honest out-of-process consumer.
    ///
    /// <para>Lazily loaded so a test that doesn't need the SDK constants (the suppression tests,
    /// the smoke test) still runs even if the SDK build output is missing — the TypeInitializer
    /// previously eagerly threw at first touch and failed every test in the class indiscriminately.</para>
    /// </summary>
    private static Assembly SdkAssembly => _sdkAssembly.Value;

    private static readonly Lazy<Assembly> _sdkAssembly = new(LoadSdkAssembly);

    private static Assembly LoadSdkAssembly()
    {
        // The Server csproj is a build-time dependency of this project (ReferenceOutputAssembly=false),
        // so the SDK assembly lives under `src/.../Sdk/bin/<Configuration>/<tfm>/` somewhere we
        // can find by walking parents from AppContext.BaseDirectory. We probe both `Debug` and
        // `Release` because CI builds Release while local `dotnet test` defaults to Debug; the
        // SDK targets netstandard2.0 today, but we glob TFM directories so the test survives a
        // future TFM bump.
        var configurations = new[] { "Debug", "Release" };
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            foreach (var configuration in configurations)
            {
                var sdkBin = new DirectoryInfo(Path.Join(
                    d.FullName,
                    "src",
                    "DevBitsLab.Mcp.SourceGraph.Sdk",
                    "bin",
                    configuration));
                if (!sdkBin.Exists) continue;
                // Pick the first TFM directory that has the SDK DLL — this project only depends
                // on the SDK's public constants, which are TFM-independent.
                var match = sdkBin
                    .EnumerateDirectories()
                    .Select(tfmDir => Path.Join(tfmDir.FullName, "DevBitsLab.Mcp.SourceGraph.Sdk.dll"))
                    .FirstOrDefault(File.Exists);
                if (match is not null)
                {
                    return Assembly.LoadFrom(match);
                }
            }
        }
        throw new FileNotFoundException(
            "Could not locate DevBitsLab.Mcp.SourceGraph.Sdk.dll under any sibling bin/{Debug,Release}/ directory; " +
            "make sure `dotnet build` ran on the SDK project before running these tests.");
    }
}
