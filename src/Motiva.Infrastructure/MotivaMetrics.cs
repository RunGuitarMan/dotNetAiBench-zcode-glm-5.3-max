using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Motiva.Infrastructure;

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
        lock (LatencyLock)
        {
            RequestLatencies.Add((kind, milliseconds));
            if (RequestLatencies.Count > 20_000)
            {
                RequestLatencies.RemoveRange(0, RequestLatencies.Count - 10_000);
            }
        }
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

    /// <summary>Prometheus text exposition: counters plus latency and bucket summaries.</summary>
    public static string RenderText()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var pair in Counters.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            builder.Append("# TYPE motiva_").Append(pair.Key.Replace(':', '_')).Append(" counter\n");
            builder.Append("motiva_").Append(pair.Key.Replace(':', '_')).Append(' ').Append(pair.Value).Append('\n');
        }

        RenderHistogram(RequestDuration, "motiva_request_duration_ms", builder);
        RenderHistogram(DbOperationDuration, "motiva_db_duration_ms", builder);
        return builder.ToString();
    }

    private static readonly object LatencyLock = new();
    private static readonly List<(string Kind, double Milliseconds)> RequestLatencies = [];

    private static void RenderHistogram(Histogram<double> histogram, string name, System.Text.StringBuilder builder)
    {
        List<(string Kind, double Milliseconds)> snapshot;
        lock (LatencyLock)
        {
            snapshot = [.. RequestLatencies];
        }

        var grouped = snapshot.GroupBy(x => x.Kind).OrderBy(g => g.Key, StringComparer.Ordinal);
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
