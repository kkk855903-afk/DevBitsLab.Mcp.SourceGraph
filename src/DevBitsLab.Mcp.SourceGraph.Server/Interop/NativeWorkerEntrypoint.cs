using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

/// <summary>
/// Hidden one-shot child-process entrypoint. It is not a trust authority: the server client must
/// authorize the repository before launching it, while this boundary independently enforces the
/// fixed protocol, request bounds, and Clang extractor's scope/privacy preflight.
/// </summary>
internal static class NativeWorkerEntrypoint
{
    public const string InvocationArgument = "--native-worker-v1";

    public static bool IsInvocation(string[] arguments) =>
        arguments.Length == 1
        && string.Equals(
            arguments[0],
            InvocationArgument,
            StringComparison.Ordinal);

    public static async Task<int> RunAsync(
        Stream standardInput,
        Stream standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default) =>
        await RunAsync(
            standardInput,
            standardOutput,
            standardError,
            static request => ClangNativeExtractor.Extract(request),
            cancellationToken).ConfigureAwait(false);

    internal static async Task<int> RunAsync(
        Stream standardInput,
        Stream standardOutput,
        TextWriter standardError,
        Func<ClangNativeExtractionRequest, ClangNativeExtractionResult> extractor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(extractor);

        NativeWorkerResponseEnvelope response;
        ClangNativeExtractionRequest? approvedRequest = null;
        var exitCode = 0;
        try
        {
            var payload = await NativeWorkerProtocol.ReadSingleFrameAsync(
                standardInput,
                NativeWorkerProtocol.MaximumRequestBytes,
                cancellationToken).ConfigureAwait(false);
            var envelope = NativeWorkerProtocol.DecodeRequest(payload);
            approvedRequest = envelope.Request;
            var extraction = extractor(envelope.Request);
            response = new NativeWorkerResponseEnvelope(
                NativeWorkerProtocol.CurrentVersion,
                NativeWorkerProtocol.ResponseKind,
                Success: true,
                extraction,
                Failure: null,
                NativeWorkerIsolationCapabilities.Baseline);
        }
        catch (NativeWorkerProtocolException ex)
        {
            response = Failure(ex.Code, ex.Message);
            exitCode = 2;
        }
        catch (OperationCanceledException)
        {
            response = Failure(
                "worker-cancelled",
                "The native worker request was cancelled.");
            exitCode = 2;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
        {
            response = Failure(
                "native-runtime-unavailable",
                "The native parser runtime is unavailable or incompatible.");
            exitCode = 3;
        }
        catch (Exception ex)
        {
            response = Failure(
                "worker-failed",
                $"The native worker failed ({ex.GetType().Name}).");
            exitCode = 3;
        }

        try
        {
            byte[] payload;
            try
            {
                payload = NativeWorkerProtocol.EncodeWorkerResponse(
                    response,
                    approvedRequest);
            }
            catch (NativeWorkerProtocolException ex)
            {
                exitCode = 3;
                payload = NativeWorkerProtocol.EncodeResponse(
                    Failure(
                        ex.Code == "response-too-large"
                            ? "response-too-large"
                            : "invalid-extraction-result",
                        ex.Code == "response-too-large"
                            ? "The native extraction result exceeded the response limit."
                            : "The native extraction result failed worker validation."));
            }

            await NativeWorkerProtocol.WriteFrameAsync(
                standardOutput,
                payload,
                NativeWorkerProtocol.MaximumResponseBytes,
                CancellationToken.None).ConfigureAwait(false);
            return exitCode;
        }
        catch (Exception ex) when (
            ex is IOException
                or ObjectDisposedException
                or NativeWorkerProtocolException)
        {
            await standardError.WriteAsync(
                $"native worker response failed ({ex.GetType().Name})")
                .ConfigureAwait(false);
            return 3;
        }
    }

    private static NativeWorkerResponseEnvelope Failure(
        string code,
        string message) =>
        new(
            NativeWorkerProtocol.CurrentVersion,
            NativeWorkerProtocol.ResponseKind,
            Success: false,
            Result: null,
            new NativeWorkerFailure(code, message),
            NativeWorkerIsolationCapabilities.Baseline);
}
