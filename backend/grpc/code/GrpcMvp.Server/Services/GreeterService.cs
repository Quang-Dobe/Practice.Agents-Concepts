// Server-side implementation. We inherit the GENERATED Greeter.GreeterBase
// (produced from greeter.proto by Grpc.Tools at build time) and override
// the two RPCs. The base class handles all the HTTP/2 framing for us.

using Grpc.Core;
using GrpcMvp.Contracts;

namespace GrpcMvp.Server.Services;

public sealed class GreeterService : Greeter.GreeterBase
{
    // UNARY: one request in, one response out.
    // The override signature is fully typed — no dictionaries, no casts.
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
    {
        // ServerCallContext gives us the deadline, peer, and metadata.
        // A real handler would propagate context.CancellationToken into any
        // downstream call so the deadline budget shrinks across hops (practice rule #3).
        return Task.FromResult(new HelloReply
        {
            Message = $"Hello, {request.Name}!",
            Sequence = 1
        });
    }

    // SERVER-STREAMING: one request in, N responses pushed back through
    // `responseStream`. The stream stays open until this method returns.
    // Each WriteAsync is one Protobuf message in one HTTP/2 DATA frame.
    public override async Task StreamGreetings(
        HelloRequest request,
        IServerStreamWriter<HelloReply> responseStream,
        ServerCallContext context)
    {
        for (var i = 1; i <= 5; i++)
        {
            // Respect cancellation: if the client hangs up or the deadline
            // fires, this throws and we exit cleanly. THIS is what makes
            // streaming gRPC sane vs ad-hoc long-poll endpoints.
            context.CancellationToken.ThrowIfCancellationRequested();

            await responseStream.WriteAsync(new HelloReply
            {
                Message = $"Hello #{i}, {request.Name}",
                Sequence = i
            });

            // Pause so the client can visibly see each message arrive
            // separately rather than as one buffered batch.
            await Task.Delay(500, context.CancellationToken);
        }
    }
}
