namespace Motiva.Application.Common;

/// <summary>Observation seam of the API surface (T06): the middleware records request
/// latency by kind and status/error counters through this abstraction; the implementation and
/// the /metrics exposition live in Infrastructure. Each named quantity measures exactly what
/// its name says — request latency here, database latency is observed at the DB layer.</summary>
public interface IRequestMetrics
{
    void ObserveRequest(string kind, double milliseconds);

    void Count(string name, long delta = 1);
}
