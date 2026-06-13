// Composition root for the gRPC server.
// Kestrel + HTTP/2 + Grpc.AspNetCore = the entire server stack.

using GrpcMvp.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Registers the gRPC routing/middleware. No JSON, no MVC — gRPC only.
builder.Services.AddGrpc();

// Force HTTP/2 cleartext (h2c) on localhost so the demo runs without TLS certs.
// In production you ALWAYS terminate TLS with ALPN h2 (see practice rule #8).
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5001, listen =>
        listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

var app = builder.Build();

// Maps the generated GreeterBase routes onto /grpcmvp.v1.Greeter/<Method>.
// The path format is /<package>.<Service>/<Method> — straight from the .proto.
app.MapGrpcService<GreeterService>();

app.Run();
