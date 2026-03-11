namespace GuardDog.Core.Models;

/// <summary>
/// Configuration bound from appsettings.json → "GuardDog" section.
/// </summary>
public sealed class GuardDogOptions
{
    public const string SectionName = "GuardDog";

    // ── Database ──────────────────────────────────────────────────────────────
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>SqlServer | PostgreSQL | MySQL</summary>
    public string Provider { get; set; } = "SqlServer";

    // ── Scheduling ────────────────────────────────────────────────────────────
    /// <summary>How often the guard-dog polls the database.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    // ── Alerting ──────────────────────────────────────────────────────────────
    public SlackOptions?  Slack  { get; set; }
    public TeamsOptions?  Teams  { get; set; }

    // ── Features ─────────────────────────────────────────────────────────────
    /// <summary>
    /// When true the worker generates a Mermaid erDiagram on every successful
    /// check and writes it to <see cref="MermaidOutputPath"/>.
    /// </summary>
    public bool   GenerateMermaidDiagram { get; set; } = true;
    public string MermaidOutputPath      { get; set; } = "docs/database.md";

    /// <summary>
    /// Only alert when <see cref="DriftSeverity"/> is at or above this threshold.
    /// Accepts "Critical", "Warning", or "Informational".
    /// </summary>
    public string AlertThreshold { get; set; } = "Warning";

    /// <summary>
    /// Path to the embedded snapshot JSON produced by GuardDog.SnapshotTool.
    /// When empty the worker uses the live EF model from the running DbContext.
    /// </summary>
    public string SnapshotPath { get; set; } = "snapshot.json";

    // ── Prometheus ────────────────────────────────────────────────────────────
    public bool EnablePrometheusMetrics { get; set; } = true;
    public int  MetricsPort             { get; set; } = 9090;
}

public sealed class SlackOptions
{
    public string WebhookUrl { get; set; } = string.Empty;
    public string Channel    { get; set; } = "#alerts";
    public string Username   { get; set; } = "Guard Dog";
}

public sealed class TeamsOptions
{
    public string WebhookUrl { get; set; } = string.Empty;
}
