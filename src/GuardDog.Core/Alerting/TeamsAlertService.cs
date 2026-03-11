using System.Net.Http.Json;
using GuardDog.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GuardDog.Core.Alerting;

/// <summary>
/// Sends an Adaptive Card to a Microsoft Teams channel via an incoming webhook.
/// The card is colour-coded and includes all drift items plus the fix-it script.
/// </summary>
public sealed class TeamsAlertService : IAlertService
{
    private readonly HttpClient _http;
    private readonly TeamsOptions _opts;
    private readonly ILogger<TeamsAlertService> _logger;

    public TeamsAlertService(
        HttpClient http,
        IOptions<GuardDogOptions> options,
        ILogger<TeamsAlertService> logger)
    {
        _http   = http;
        _opts   = options.Value.Teams ?? throw new InvalidOperationException("Teams options are not configured.");
        _logger = logger;
    }

    public async Task SendAsync(DriftReport report, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.WebhookUrl))
        {
            _logger.LogWarning("Teams WebhookUrl is not configured. Skipping alert.");
            return;
        }

        var payload = BuildPayload(report);
        var response = await _http.PostAsJsonAsync(_opts.WebhookUrl, payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Teams webhook returned {Status}: {Body}", response.StatusCode, body);
        }
        else
        {
            _logger.LogInformation("Teams alert sent for drift report at {CheckedAt}", report.CheckedAt);
        }
    }

    private static object BuildPayload(DriftReport report)
    {
        var themeColour = report.HasCriticalDrift ? "FF0000" : "FFA500";
        var titleEmoji  = report.HasCriticalDrift ? "🚨" : "⚠️";

        // Build facts (table + description for each item, capped at 15 for readability)
        var facts = report.Items
            .OrderByDescending(i => i.Severity)
            .Take(15)
            .Select(i => new
            {
                name  = $"{SeverityIcon(i.Severity)} {i.Kind} — {i.FullTableName}.{i.ObjectName}",
                value = $"Code: `{Truncate(i.CodeState, 60)}` → DB: `{Truncate(i.DatabaseState, 60)}`"
            })
            .ToList();

        if (report.Items.Count > 15)
            facts.Add(new
            {
                name  = "…",
                value = $"and {report.Items.Count - 15} more items"
            });

        var sections = new List<object>
        {
            new
            {
                activityTitle    = $"{titleEmoji} Database Drift Detected",
                activitySubtitle = $"{report.DataSource} · {report.DatabaseProvider} · " +
                                   $"Snapshot v{report.SnapshotVersion}",
                activityText     = $"Checked at **{report.CheckedAt:yyyy-MM-dd HH:mm:ss} UTC** · " +
                                   $"**{report.Items.Count}** issue(s) found " +
                                   $"(🔴 {report.CriticalItems.Count()} critical, " +
                                   $"🟡 {report.WarningItems.Count()} warnings, " +
                                   $"🔵 {report.InformationalItems.Count()} info)",
                facts
            }
        };

        if (report.AggregatedFixScript is { } fixScript)
            sections.Add(new
            {
                activityTitle = "🔧 Fix-It Script",
                activityText  = $"```sql\n{Truncate(fixScript, 3000)}\n```"
            });

        // Microsoft Teams Legacy Connector Card format (widely supported)
        return new
        {
            type        = "MessageCard",
            context     = "https://schema.org/extensions",
            themeColor  = themeColour,
            summary     = $"Database drift detected: {report.Items.Count} issue(s)",
            sections
        };
    }

    private static string SeverityIcon(DriftSeverity severity) => severity switch
    {
        DriftSeverity.Critical      => "🔴",
        DriftSeverity.Warning       => "🟡",
        DriftSeverity.Informational => "🔵",
        _ => "⚪"
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
