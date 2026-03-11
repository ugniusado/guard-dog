using System.Diagnostics;
using System.Text.Json;
using GuardDog.Core.Alerting;
using GuardDog.Core.Diagram;
using GuardDog.Core.DriftEngine;
using GuardDog.Core.Metrics;
using GuardDog.Core.Models;
using GuardDog.Core.Schema;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GuardDog.Worker;

/// <summary>
/// The main background worker.  On each tick it:
///   1. Loads the embedded <see cref="SchemaSnapshot"/> (produced at CI/CD build time)
///   2. Reads the live database schema via INFORMATION_SCHEMA queries
///   3. Compares them with the <see cref="IDriftDetector"/>
///   4. Sends alerts if drift is found and meets the severity threshold
///   5. Writes the Mermaid ER diagram to disk if enabled
///   6. Records Prometheus metrics
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly GuardDogOptions _opts;
    private readonly IDatabaseSchemaReader _schemaReader;
    private readonly IDriftDetector _driftDetector;
    private readonly IAlertService _alertService;
    private readonly IMermaidGenerator _mermaidGenerator;

    private SchemaSnapshot? _snapshot;
    private DriftSeverity _threshold;

    public Worker(
        ILogger<Worker> logger,
        IOptions<GuardDogOptions> options,
        IDatabaseSchemaReader schemaReader,
        IDriftDetector driftDetector,
        IAlertService alertService,
        IMermaidGenerator mermaidGenerator)
    {
        _logger           = logger;
        _opts             = options.Value;
        _schemaReader     = schemaReader;
        _driftDetector    = driftDetector;
        _alertService     = alertService;
        _mermaidGenerator = mermaidGenerator;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        _threshold = Enum.TryParse<DriftSeverity>(_opts.AlertThreshold, true, out var t)
            ? t
            : DriftSeverity.Warning;

        _snapshot = await LoadSnapshotAsync(ct);

        _logger.LogInformation(
            "Guard Dog started. Provider={Provider} Interval={Interval} Threshold={Threshold} " +
            "Snapshot={SnapshotVersion} Tables={TableCount}",
            _opts.Provider,
            _opts.CheckInterval,
            _threshold,
            _snapshot.Version,
            _snapshot.Tables.Count);

        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first check by 5 s to allow the host to fully start
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCheckAsync(stoppingToken);
            await Task.Delay(_opts.CheckInterval, stoppingToken);
        }
    }

    // ── Core check logic ──────────────────────────────────────────────────────

    private async Task RunCheckAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var dataSource = ExtractDataSource(_opts.ConnectionString);
            _logger.LogInformation("Starting schema check against {DataSource}...", dataSource);

            // 1. Read live DB schema
            var liveSchema = await _schemaReader.ReadAsync(_opts.ConnectionString, ct);

            // 2. Compare
            var report = _driftDetector.Detect(_snapshot!, liveSchema, _opts.Provider, dataSource);

            sw.Stop();

            // 3. Record metrics
            GuardDogMetrics.Instance.CheckDuration.Observe(sw.Elapsed.TotalSeconds);
            GuardDogMetrics.Instance.RecordCheckResult(
                result:       report.HasDrift ? "drift" : "clean",
                provider:     _opts.Provider,
                dataSource:   dataSource,
                hasDrift:     report.HasDrift,
                criticalCount: report.CriticalItems.Count(),
                warningCount:  report.WarningItems.Count(),
                infoCount:     report.InformationalItems.Count());

            if (report.HasDrift)
            {
                GuardDogMetrics.Instance.RecordDriftItems(
                    report.Items.Select(i => (i.Severity.ToString(), i.Kind.ToString())));
            }

            // 4. Log result
            if (!report.HasDrift)
            {
                _logger.LogInformation(
                    "Check complete in {Elapsed:F2}s. Database is in sync. No drift detected.",
                    sw.Elapsed.TotalSeconds);
            }
            else
            {
                _logger.LogWarning(
                    "Check complete in {Elapsed:F2}s. DRIFT DETECTED: {Total} issue(s). " +
                    "Critical={Critical} Warning={Warning} Informational={Info}",
                    sw.Elapsed.TotalSeconds,
                    report.Items.Count,
                    report.CriticalItems.Count(),
                    report.WarningItems.Count(),
                    report.InformationalItems.Count());

                foreach (var item in report.CriticalItems)
                    _logger.LogError("  {Item}", item);
                foreach (var item in report.WarningItems)
                    _logger.LogWarning("  {Item}", item);
            }

            // 5. Send alert if any item is at or above the configured threshold
            var shouldAlert = report.Items.Any(i => i.Severity <= _threshold);
            if (shouldAlert)
                await _alertService.SendAsync(report, ct);

            // 6. Regenerate Mermaid diagram
            if (_opts.GenerateMermaidDiagram)
                await WriteMermaidDiagramAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on graceful shutdown
        }
        catch (Exception ex)
        {
            sw.Stop();
            GuardDogMetrics.Instance.ChecksTotal.WithLabels("error").Inc();
            _logger.LogError(ex, "Schema check failed after {Elapsed:F2}s.", sw.Elapsed.TotalSeconds);
        }
    }

    // ── Snapshot loading (Phase 2 — Shadow Schema) ────────────────────────────

    private async Task<SchemaSnapshot> LoadSnapshotAsync(CancellationToken ct)
    {
        // 1. Try file path (useful for local dev / volume-mounted CI artifacts)
        if (File.Exists(_opts.SnapshotPath))
        {
            var json = await File.ReadAllTextAsync(_opts.SnapshotPath, ct);
            var snapshot = JsonSerializer.Deserialize<SchemaSnapshot>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (snapshot is not null)
            {
                _logger.LogInformation("Loaded snapshot v{Version} from '{Path}' ({Tables} tables)",
                    snapshot.Version, _opts.SnapshotPath, snapshot.Tables.Count);
                return snapshot;
            }
        }

        // 2. Try embedded resource compiled into the assembly by GuardDog.SnapshotTool
        var assembly     = typeof(Worker).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("snapshot.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)!;
            var snapshot = await JsonSerializer.DeserializeAsync<SchemaSnapshot>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

            if (snapshot is not null)
            {
                _logger.LogInformation("Loaded embedded snapshot v{Version} ({Tables} tables)",
                    snapshot.Version, snapshot.Tables.Count);
                return snapshot;
            }
        }

        _logger.LogWarning(
            "No snapshot found at '{Path}' and no embedded assembly resource. " +
            "Run GuardDog.SnapshotTool during CI/CD to generate one. " +
            "Proceeding with empty snapshot — all DB tables will appear as 'extra'.",
            _opts.SnapshotPath);

        return SchemaSnapshot.Empty;
    }

    // ── Mermaid diagram output (Phase 4) ──────────────────────────────────────

    private async Task WriteMermaidDiagramAsync(CancellationToken ct)
    {
        try
        {
            var diagram = _mermaidGenerator.Generate(_snapshot!);
            var dir     = Path.GetDirectoryName(_opts.MermaidOutputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(_opts.MermaidOutputPath, diagram, ct);
            _logger.LogDebug("Mermaid diagram written to {Path}", _opts.MermaidOutputPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write Mermaid diagram to {Path}", _opts.MermaidOutputPath);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts host + database name from the connection string for logging.
    /// Never returns credentials.
    /// </summary>
    private static string ExtractDataSource(string connectionString)
    {
        try
        {
            var parts = connectionString
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.StartsWith("Server=",         StringComparison.OrdinalIgnoreCase)
                         || p.StartsWith("Host=",           StringComparison.OrdinalIgnoreCase)
                         || p.StartsWith("Data Source=",    StringComparison.OrdinalIgnoreCase)
                         || p.StartsWith("Database=",       StringComparison.OrdinalIgnoreCase)
                         || p.StartsWith("Initial Catalog=",StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Split('=', 2).Last());

            return string.Join("/", parts);
        }
        catch
        {
            return "unknown";
        }
    }
}
