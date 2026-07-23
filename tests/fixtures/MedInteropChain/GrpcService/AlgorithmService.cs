using MedInteropChain.GrpcService.Generated;

namespace MedInteropChain.GrpcService;

public sealed class AlgorithmService(AlgorithmApi.AlgorithmApiClient client)
{
    public async Task<int> CalculateAsync(int patientAge, CancellationToken cancellationToken = default)
    {
        var reply = await client.CalculateAsync(
            new CalculateRequest { PatientAge = patientAge },
            cancellationToken);
        return reply.Value;
    }
}
