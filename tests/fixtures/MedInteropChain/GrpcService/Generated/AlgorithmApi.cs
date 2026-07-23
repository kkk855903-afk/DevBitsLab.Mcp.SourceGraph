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
    public abstract class AlgorithmApiBase
    {
        public virtual Task<CalculateReply> Calculate(
            CalculateRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CalculateReply>(new NotImplementedException());
    }

    public class AlgorithmApiClient
    {
        public virtual Task<CalculateReply> CalculateAsync(
            CalculateRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CalculateReply { Value = request.PatientAge });
    }
}
