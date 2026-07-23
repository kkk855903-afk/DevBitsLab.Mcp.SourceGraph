using MedInteropChain.GrpcService.Generated;
using MedInteropChain.GrpcService.Interop;

namespace MedInteropChain.GrpcService;

public sealed class AlgorithmGrpcService : AlgorithmApi.AlgorithmApiBase
{
    public override Task<CalculateReply> Calculate(
        CalculateRequest request,
        CancellationToken cancellationToken = default)
    {
        var input = new NativeInput { PatientAge = request.PatientAge, Scale = 1.0 };
        var status = NativeMethods.Calculate(in input, out var output);
        if (status != 0) throw new InvalidOperationException($"native status {status}");
        return Task.FromResult(new CalculateReply { Value = output.Value });
    }
}
