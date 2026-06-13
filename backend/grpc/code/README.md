# gRPC — MVP Code

Smallest runnable gRPC demo in .NET: one `.proto` defines a `Greeter` service
with **one unary RPC and one server-streaming RPC**; a server implements them;
a client calls both over a single HTTP/2 channel. ~70 lines of actual code —
the rest is generated from `proto/greeter.proto` by `Grpc.Tools`.

## What it demonstrates

- **Schema-first** — `proto/greeter.proto` is the single source of truth; Server generates `GreeterBase`, Client generates `GreeterClient`, from the same file.
- **Unary** (`SayHello`) — one HTTP/2 stream, one Protobuf request, one Protobuf response; looks like a local async call.
- **Server-streaming** (`StreamGreetings`) — one request, N responses pushed back over the same stream, consumed with `await foreach`. No WebSocket plumbing.
- **One channel, many calls** — practice rule #12: a single `GrpcChannel` is reused for both RPCs.

Production hardening (TLS/mTLS, deadlines, retries, interceptors, LB, reflection-gating, size caps) is covered in `../docs/03-practice.md` and left out here.

## Prerequisites

**.NET SDK 8.0+**. Internet for the first `dotnet restore`. No Docker, no other services.

## Run it

Open **two terminals**, both in this `code/` directory.

```bash
dotnet run --project GrpcMvp.Server   # terminal 1 — http://localhost:5001 (h2c)
dotnet run --project GrpcMvp.Client   # terminal 2 — after server is listening
```

## Expected output (client terminal)

```
[unary] Hello, world!
[stream #1] Hello #1, world
...
[stream #5] Hello #5, world
done.
```

The `[stream #N]` lines arrive ~500 ms apart — visible proof they're streamed, not buffered.

## What to try next

- Change `int32 sequence = 2;` to `int32 sequence = 3;` in `greeter.proto`, rebuild only the client — watch field 3 fail to decode into the server's field 2 (practice rule #2 live).
- Add `deadline: DateTime.UtcNow.AddMilliseconds(100)` to `SayHelloAsync` and bump the server delay past it — observe `Status(StatusCode=DeadlineExceeded)`.
- `break` out of the `await foreach` after the 2nd message — the server's next `WriteAsync` throws `OperationCanceledException`.
- Stop the server mid-stream — client sees `Status(StatusCode=Unavailable)`, not a hung process. HTTP/2 trailers doing their job.
