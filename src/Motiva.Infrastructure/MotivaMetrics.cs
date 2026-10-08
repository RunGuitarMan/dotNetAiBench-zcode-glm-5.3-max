using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Motiva.Application.Common;

namespace Motiva.Infrastructure;

/// <summary>Lightweight metric registry (T06): request latency by kind, REAL database command
/// latency by operation (measured by <see cref="DbCommandTimingInterceptor"/>), error counts,
/// outbox queue depth; exposed in Prometheus text format at /metrics. Each histogram keeps its
/// own samples — a DB duration series never reuses HTTP measurements.</summary>
public static class MotivaMetrics
{
    public const string MeterName = "Motiva";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly ConcurrentDictionary<string, long> Counters = new();
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "motiva_request_duration_ms", "ms", "HTTP request duration by operation kind");
    private static readonly Histogram<double> DbOperationDuration = Meter.CreateHistogram<double>(
        "motiva_db_duration_ms", "ms", "Database command duration by operation");

    private static readonly ObservableGauge<double> QueueDepth = Meter.CreateObservableGauge(
        "motiva_outbox_pending", () => new Measurement<double>(LoadCounter("outbox:pending")), "jobs", "Outbox jobs waiting");

    private static readonly object LatencyLock = new();
    private static readonly List<(string Kind, double Milliseconds)> RequestLatencies = [];
    private static readonly List<(string Operation, double Milliseconds)> DbLatencies = [];

    public static void ObserveRequest(string kind, double milliseconds)
    {
        RequestDuration.Record(milliseconds, new KeyValuePair<string, object?>("kind", kind));
        lock (LatencyLock)
        {
            RequestLatencies.Add((kind, milliseconds));
            if (RequestLatencies.Count > 20_000)
            {
                RequestLatencies.RemoveRange(0, RequestLatencies.Count - 10_000);
            }
        }
    }

    public static void ObserveDb(string operation, double milliseconds)
    {
        DbOperationDuration.Record(milliseconds, new KeyValuePair<string, object?>("operation", operation));
        lock (LatencyLock)
        {
            DbLatencies.Add((operation, milliseconds));
            if (DbLatencies.Count > 20_000)
            {
                DbLatencies.RemoveRange(0, DbLatencies.Count - 10_000);
            }
        }
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

    /// <summary>Prometheus text exposition: counters plus latency and bucket summaries.</summary>
    public static string RenderText()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var pair in Counters.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            builder.Append("# TYPE motiva_").Append(pair.Key.Replace(':', '_')).Append(" counter\n");
            builder.Append("motiva_").Append(pair.Key.Replace(':', '_')).Append(' ').Append(pair.Value).Append('\n');
        }

        List<(string Kind, double Milliseconds)> requests;
        List<(string Operation, double Milliseconds)> db;
        lock (LatencyLock)
        {
            requests = [.. RequestLatencies];
            db = [.. DbLatencies];
        }

        RenderSummary("motiva_request_duration_ms", requests.Select(x => (x.Kind, x.Milliseconds)), builder);
        RenderSummary("motiva_db_duration_ms", db.Select(x => (x.Operation, x.Milliseconds)), builder);
        return builder.ToString();
    }

    private static void RenderSummary(string name, IEnumerable<(string Label, double Milliseconds)> samples, System.Text.StringBuilder builder)
    {
        var grouped = samples.GroupBy(x => x.Label).OrderBy(g => g.Key, StringComparer.Ordinal);
        builder.Append("# TYPE ").Append(name).Append(" summary\n");
        foreach (var group in grouped)
        {
            var sorted = group.Select(x => x.Milliseconds).OrderBy(v => v).ToList();
            if (sorted.Count == 0)
            {
                continue;
            }

            double Quantile(double q) => sorted[(int)Math.Min(sorted.Count - 1, Math.Round(q * (sorted.Count - 1)))];
            builder.Append(name).Append("{kind=\"").Append(group.Key).Append("\",quantile=\"0.5\"} ")
                .Append(Quantile(0.5).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(name).Append("{kind=\"").Append(group.Key).Append("\",quantile=\"0.95\"} ")
                .Append(Quantile(0.95).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(name).Append("{kind=\"").Append(group.Key).Append("\",quantile=\"0.99\"} ")
                .Append(Quantile(0.99).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(name).Append("_count{kind=\"").Append(group.Key).Append("\"} ").Append(sorted.Count).Append('\n');
        }
    }
}

/// <summary>DI adapter: the Api middleware observes through the application-level seam without
/// referencing Infrastructure (L01); the static registry stays process-wide.</summary>
public sealed class RequestMetricsSink : IRequestMetrics
{
    public void ObserveRequest(string kind, double milliseconds) => MotivaMetrics.ObserveRequest(kind, milliseconds);

    public void Count(string name, long delta = 1) => MotivaMetrics.Count(name, delta);
}
