using System.Threading;
using System.Threading.Tasks;
using GrpcToolsGenerated.Generated;

namespace GrpcToolsGenerated;

public sealed class ClientFacade(EchoApi.EchoApiClient client)
{
    public async Task<string> SendAsync(
        string value,
        CancellationToken cancellationToken = default)
    {
        var reply = await client.EchoAsync(
            new EchoRequest { Value = value },
            cancellationToken: cancellationToken);
        return reply.Value;
    }
}
