using Application.Abstractions;
using Application.Mediator;

namespace Application.Workload;

// Fire `Operations` concurrent "queries" through whatever IConnectionPool is
// wired up, capped by `Concurrency`. The report is what the caller prints.
public sealed record RunWorkloadCommand(
    int Operations,
    int Concurrency,
    TimeSpan AcquireTimeout) : IRequest<WorkloadReport>;

public sealed record WorkloadReport(
    int Operations,
    TimeSpan Elapsed,
    PoolStats Stats);
