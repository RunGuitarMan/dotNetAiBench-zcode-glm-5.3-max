using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Motiva.Api.Infrastructure;

/// <summary>Lightweight metric registry (T06): request latency by kind, error counts, economic
/// operation outcomes, outbox queue depth; exposed in Prometheus text format at /metrics.</summary>
public static class MotivaMetrics
{
    public const string MeterName = "Motiva";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly ConcurrentDictionary<string, long> Counters = new();
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "motiva_request_duration_ms", "ms", "HTTP request duration by operation kind");
    private static readonly Histogram<double> DbOperationDuration = Meter.CreateHistogram<double>(
        "motiva_db_duration_ms", "ms", "Database operation duration");

    private static readonly ObservableGauge<double> QueueDepth = Meter.CreateObservableGauge(
        "motiva_outbox_pending", () => new Measurement<double>(LoadCounter("outbox:pending")), "jobs", "Outbox jobs waiting");

    public static void ObserveRequest(string kind, double milliseconds)
    {
        RequestDuration.Record(milliseconds, new KeyValuePair<string, object?>("kind", kind));
    }

    public static void ObserveDb(double milliseconds, string operation)
    {
        DbOperationDuration.Record(milliseconds, new KeyValuePair<string, object?>("operation", operation));
    }

    public static void Count(string name, long delta = 1)
    {
        Counters.AddOrUpdate(name, delta, (_, current) => current + delta);
    }

    public static void SetGauge(string name, long value)
    {
        Counters[name] = value;
    }

    private static long LoadCounter(string name)
    {
        return Counters.TryGetValue(name, out var value) ? value : 0;
    }

    /// <summary>Prometheus text exposition of counters and histograms registered so far.</summary>
    public static string RenderText()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var pair in Counters.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            builder.Append("# TYPE motiva_").Append(pair.Key.Replace(':', '_')).Append(" counter\n");
            builder.Append("motiva_").Append(pair.Key.Replace(':', '_')).Append(' ').Append(pair.Value).Append('\n');
        }

        return builder.ToString();
    }
}
