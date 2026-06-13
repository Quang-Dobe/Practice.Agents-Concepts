// Client side. The generated GreeterClient makes a remote call look local.
// We open ONE channel, reuse it for both RPCs (practice rule #12),
// and tear down at the end.

using Grpc.Net.Client;
using GrpcMvp.Contracts;

// The channel is the long-lived HTTP/2 connection abstraction.
// http://  (not https://) because the server runs in h2c mode on localhost.
// AppContext switch is required to allow cleartext HTTP/2 on .NET.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
using var channel = GrpcChannel.ForAddress("http://localhost:5001");

// The generated stub is a thin wrapper over the channel. Stateless.
// Constructing it per-call is fine; constructing a CHANNEL per-call is not.
var greeter = new Greeter.GreeterClient(channel);

// -------- UNARY CALL --------
// Looks like a local async method. Under the hood: one HTTP/2 stream,
// one Protobuf request frame, one response frame, one trailers frame
// carrying grpc-status: 0. See deep-dive doc, "How it works under the hood."
var reply = await greeter.SayHelloAsync(new HelloRequest { Name = "world" });
Console.WriteLine($"[unary] {reply.Message}");

// -------- SERVER-STREAMING CALL --------
// SayHello returned a Task<HelloReply>. StreamGreetings returns an
// AsyncServerStreamingCall — its ResponseStream is an async iterator.
// Each iteration awaits the NEXT message off the wire; the loop ends
// when the server sets END_STREAM on its trailers frame.
using var stream = greeter.StreamGreetings(new HelloRequest { Name = "world" });
await foreach (var msg in stream.ResponseStream.ReadAllAsync())
{
    Console.WriteLine($"[stream #{msg.Sequence}] {msg.Message}");
}

Console.WriteLine("done.");
