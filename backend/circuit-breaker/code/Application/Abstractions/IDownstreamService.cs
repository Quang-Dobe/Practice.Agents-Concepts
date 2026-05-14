namespace Application.Abstractions;

// The "remote dependency" port. In a real app this would be backed by HttpClient,
// a gRPC stub, or a database driver. The Application layer doesn't care which.
public interface IDownstreamService
{
    Task<string> Call(CancellationToken ct);
}
