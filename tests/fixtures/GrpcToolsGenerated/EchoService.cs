using System.Threading.Tasks;
using Grpc.Core;
using GrpcToolsGenerated.Generated;

namespace GrpcToolsGenerated;

public sealed class EchoService : EchoApi.EchoApiBase
{
    public override Task<EchoReply> Echo(
        EchoRequest request,
        ServerCallContext context) =>
        Task.FromResult(new EchoReply { Value = request.Value });
}
