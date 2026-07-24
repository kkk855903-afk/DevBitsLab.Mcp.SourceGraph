using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;

namespace MedInteropChain.GrpcService.Generated;

public sealed class CalculateRequest
{
    public int PatientAge { get; init; }
}

public sealed class CalculateReply
{
    public int Value { get; init; }
}

public static class AlgorithmApi
{
    private static readonly string __ServiceName =
        "medinterop.algorithm.v1.AlgorithmApi";

    private static readonly Method<CalculateRequest, CalculateReply>
        __Method_Calculate = new(
            MethodType.Unary,
            __ServiceName,
            "Calculate",
            new Marshaller<CalculateRequest>(),
            new Marshaller<CalculateReply>());

    public abstract class AlgorithmApiBase
    {
        public virtual Task<CalculateReply> Calculate(
            CalculateRequest request,
            ServerCallContext context) =>
            Task.FromException<CalculateReply>(new NotImplementedException());
    }

    public class AlgorithmApiClient : ClientBase<AlgorithmApiClient>
    {
        public virtual AsyncUnaryCall<CalculateReply> CalculateAsync(
            CalculateRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            InvokeUnary(request, __Method_Calculate);

        public virtual AsyncUnaryCall<CalculateReply> CalculateAsync(
            CalculateRequest request,
            CallOptions options) =>
            InvokeUnary(request, __Method_Calculate);

        private static AsyncUnaryCall<CalculateReply> InvokeUnary(
            CalculateRequest request,
            Method<CalculateRequest, CalculateReply> method) =>
            new(Task.FromResult(
                new CalculateReply
                {
                    Value = method.Type == MethodType.Unary
                        ? request.PatientAge
                        : 0,
                }));
    }
}
