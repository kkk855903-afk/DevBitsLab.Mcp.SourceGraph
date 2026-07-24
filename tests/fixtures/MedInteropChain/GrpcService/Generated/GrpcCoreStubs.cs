using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Grpc.Core;

public enum MethodType
{
    Unary,
    ClientStreaming,
    ServerStreaming,
    DuplexStreaming,
}

public sealed class Marshaller<T>;

public sealed class Method<TRequest, TResponse>(
    MethodType type,
    string serviceName,
    string name,
    Marshaller<TRequest> requestMarshaller,
    Marshaller<TResponse> responseMarshaller)
{
    public MethodType Type { get; } = type;
    public string ServiceName { get; } = serviceName;
    public string Name { get; } = name;
    public Marshaller<TRequest> RequestMarshaller { get; } =
        requestMarshaller;
    public Marshaller<TResponse> ResponseMarshaller { get; } =
        responseMarshaller;
}

public sealed class Metadata;

public readonly struct CallOptions;

public abstract class ClientBase<TClient>
    where TClient : ClientBase<TClient>;

public abstract class ServerCallContext
{
    public abstract CancellationToken CancellationToken { get; }
}

public sealed class AsyncUnaryCall<TResponse>(Task<TResponse> responseAsync)
{
    public Task<TResponse> ResponseAsync { get; } = responseAsync;

    public TaskAwaiter<TResponse> GetAwaiter() =>
        ResponseAsync.GetAwaiter();
}

public sealed class AsyncServerStreamingCall<TResponse>;

public sealed class AsyncClientStreamingCall<TRequest, TResponse>;

public sealed class AsyncDuplexStreamingCall<TRequest, TResponse>;

public interface IAsyncStreamReader<out T>;

public interface IServerStreamWriter<in T>;
