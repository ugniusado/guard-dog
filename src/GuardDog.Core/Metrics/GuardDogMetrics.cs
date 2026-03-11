using Prometheus;

// Use alias to avoid ambiguity with the GuardDog.Core.Metrics namespace
using PM = Prometheus.Metrics;

namespace GuardDog.Core.Metrics;

/// <summary>
/// Prometheus metrics exported by the Guard Dog worker.
///
/// Grafana dashboard query examples:
///   • db_drift_detected{provider="SqlServer"}        → 0 or 1 per database
///   • db_drift_items_current{severity="Critical"}    → count from latest check
///   • db_check_duration_seconds                      → check latency histogram
///   • db_checks_total{result="drift"|"clean"|"error"} → total check outcomes
/// </summary>
public sealed class GuardDogMetrics
{
    // ── Gauges ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 1 when the last check found drift, 0 when clean.
    /// Use this as your primary alerting signal in Grafana.
    /// </summary>
    public readonly Gauge DriftDetected = PM.CreateGauge(
        "db_drift_detected",
        "1 if the last Guard Dog check detected schema drift, 0 if the database is in sync.",
        new GaugeConfiguration { LabelNames = ["provider", "data_source"] });

    /// <summary>Current count of drift items by severity from the most recent check.</summary>
    public readonly Gauge DriftItemsBySeverity = PM.CreateGauge(
        "db_drift_items_current",
        "Number of drift items by severity in the most recent check.",
        new GaugeConfiguration { LabelNames = ["severity", "provider"] });

    // ── Counters ──────────────────────────────────────────────────────────────

    /// <summary>Total number of completed checks since process start.</summary>
    public readonly Counter ChecksTotal = PM.CreateCounter(
        "db_checks_total",
        "Total number of Guard Dog checks performed since process start.",
        new CounterConfiguration { LabelNames = ["result"] }); // result: drift | clean | error

    /// <summary>Total number of drift items ever reported by severity.</summary>
    public readonly Counter DriftItemsTotal = PM.CreateCounter(
        "db_drift_items_total",
        "Cumulative number of drift items reported since process start.",
        new CounterConfiguration { LabelNames = ["severity", "kind"] });

    // ── Histogram ─────────────────────────────────────────────────────────────

    /// <summary>Time taken to complete a full schema check (DB read + comparison).</summary>
    public readonly Histogram CheckDuration = PM.CreateHistogram(
        "db_check_duration_seconds",
        "Time in seconds to complete a full Guard Dog schema check.",
        new HistogramConfiguration
        {
            Buckets = Histogram.LinearBuckets(start: 0.1, width: 0.5, count: 10)
        });

    // ── Singleton ─────────────────────────────────────────────────────────────

    public static readonly GuardDogMetrics Instance = new();
    private GuardDogMetrics() { }

    // ── Convenience methods ───────────────────────────────────────────────────

    public void RecordCheckResult(
        string result,
        string provider,
        string dataSource,
        bool hasDrift,
        int criticalCount,
        int warningCount,
        int infoCount)
    {
        ChecksTotal.WithLabels(result).Inc();

        DriftDetected
            .WithLabels(provider, dataSource)
            .Set(hasDrift ? 1 : 0);

        DriftItemsBySeverity.WithLabels("Critical",      provider).Set(criticalCount);
        DriftItemsBySeverity.WithLabels("Warning",       provider).Set(warningCount);
        DriftItemsBySeverity.WithLabels("Informational", provider).Set(infoCount);
    }

    public void RecordDriftItems(IEnumerable<(string Severity, string Kind)> items)
    {
        foreach (var (severity, kind) in items)
            DriftItemsTotal.WithLabels(severity, kind).Inc();
    }
}
