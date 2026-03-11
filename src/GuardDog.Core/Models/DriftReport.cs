namespace GuardDog.Core.Models;

/// <summary>
/// The full result of a single guard-dog check run.
/// </summary>
public sealed record DriftReport
{
    public required DateTimeOffset CheckedAt     { get; init; }
    public required string DatabaseProvider      { get; init; }
    public required string DataSource            { get; init; }   // host/db name, never creds
    public required string SnapshotVersion       { get; init; }

    public required IReadOnlyList<DriftItem> Items { get; init; }

    // ── Convenience properties ────────────────────────────────────────────────
    public bool HasDrift         => Items.Count > 0;
    public bool HasCriticalDrift => Items.Any(i => i.Severity == DriftSeverity.Critical);
    public bool HasWarnings      => Items.Any(i => i.Severity == DriftSeverity.Warning);

    public IEnumerable<DriftItem> CriticalItems =>
        Items.Where(i => i.Severity == DriftSeverity.Critical);
    public IEnumerable<DriftItem> WarningItems =>
        Items.Where(i => i.Severity == DriftSeverity.Warning);
    public IEnumerable<DriftItem> InformationalItems =>
        Items.Where(i => i.Severity == DriftSeverity.Informational);

    /// <summary>Mermaid erDiagram for the code model (populated when enabled).</summary>
    public string? MermaidDiagram { get; init; }

    /// <summary>
    /// Aggregated SQL fix script for all Critical/Warning items that have an
    /// automated fix. Null when there is no drift or no safe scripts exist.
    /// </summary>
    public string? AggregatedFixScript
    {
        get
        {
            var scripts = Items
                .Where(i => i.FixScript is not null)
                .Select(i => $"-- [{i.Severity}] {i.Kind}: {i.FullTableName}.{i.ObjectName}\n{i.FixScript}")
                .ToList();

            return scripts.Count > 0
                ? string.Join("\n\n", scripts)
                : null;
        }
    }

    public override string ToString() =>
        HasDrift
            ? $"DRIFT DETECTED at {CheckedAt:u}: {Items.Count} issue(s), " +
              $"critical={CriticalItems.Count()}, warnings={WarningItems.Count()}"
            : $"No drift at {CheckedAt:u}. DB is in sync with snapshot v{SnapshotVersion}.";
}
