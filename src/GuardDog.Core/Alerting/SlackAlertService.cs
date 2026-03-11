using System.Net.Http.Json;
using System.Text.Json;
using GuardDog.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GuardDog.Core.Alerting;

/// <summary>
/// Sends a structured Slack Block Kit message when drift is detected.
/// The message includes:
///   • A colour-coded header (red for Critical, yellow for Warning)
///   • A bulleted list of every drift item with its severity emoji
///   • The aggregated SQL fix-it script as a code block (if any)
/// </summary>
public sealed class SlackAlertService : IAlertService
{
    private readonly HttpClient _http;
    private readonly SlackOptions _opts;
    private readonly ILogger<SlackAlertService> _logger;

    public SlackAlertService(
        HttpClient http,
        IOptions<GuardDogOptions> options,
        ILogger<SlackAlertService> logger)
    {
        _http   = http;
        _opts   = options.Value.Slack ?? throw new InvalidOperationException("Slack options are not configured.");
        _logger = logger;
    }

    public async Task SendAsync(DriftReport report, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.WebhookUrl))
        {
            _logger.LogWarning("Slack WebhookUrl is not configured. Skipping alert.");
            return;
        }

        var payload = BuildPayload(report);

        var response = await _http.PostAsJsonAsync(_opts.WebhookUrl, payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Slack webhook returned {Status}: {Body}",
                response.StatusCode, body);
        }
        else
        {
            _logger.LogInformation("Slack alert sent successfully for drift report at {CheckedAt}",
                report.CheckedAt);
        }
    }

    private object BuildPayload(DriftReport report)
    {
        var colour  = report.HasCriticalDrift ? "#FF0000" : "#FFA500";
        var icon    = report.HasCriticalDrift ? ":rotating_light:" : ":warning:";
        var title   = report.HasCriticalDrift
            ? $"{icon} CRITICAL DATABASE DRIFT DETECTED"
            : $"{icon} Database Drift Detected";

        var lines = report.Items
            .OrderByDescending(i => i.Severity)
            .Take(20)   // Slack has block limits
            .Select(i => $"{SeverityEmoji(i.Severity)} *{i.Kind}* on `{i.FullTableName}.{i.ObjectName}`\n" +
                         $">Code: `{Truncate(i.CodeState, 80)}`\n" +
                         $">DB:   `{Truncate(i.DatabaseState, 80)}`");

        var blocks = new List<object>
        {
            new { type = "header", text = new { type = "plain_text", text = title, emoji = true } },
            new { type = "section", text = new { type = "mrkdwn",
                text = $"*Database:* `{report.DataSource}`  •  " +
                       $"*Provider:* {report.DatabaseProvider}  •  " +
                       $"*Snapshot:* v{report.SnapshotVersion}\n" +
                       $"*Checked:* {report.CheckedAt:yyyy-MM-dd HH:mm:ss} UTC\n" +
                       $"*Total issues:* {report.Items.Count} " +
                       $"(Critical: {report.CriticalItems.Count()}, " +
                       $"Warning: {report.WarningItems.Count()}, " +
                       $"Info: {report.InformationalItems.Count()})" }},
            new { type = "divider" }
        };

        foreach (var line in lines)
            blocks.Add(new { type = "section", text = new { type = "mrkdwn", text = line } });

        if (report.Items.Count > 20)
            blocks.Add(new { type = "section", text = new { type = "mrkdwn",
                text = $"_...and {report.Items.Count - 20} more items. Run the Guard Dog locally for the full report._" }});

        if (report.AggregatedFixScript is { } fixScript)
        {
            blocks.Add(new { type = "divider" });
            blocks.Add(new { type = "section", text = new { type = "mrkdwn",
                text = $":wrench: *Fix-It Script*\n```{Truncate(fixScript, 2500)}```" }});
        }

        return new
        {
            username    = _opts.Username,
            channel     = _opts.Channel,
            attachments = new[]
            {
                new { color = colour, blocks }
            }
        };
    }

    private static string SeverityEmoji(DriftSeverity severity) => severity switch
    {
        DriftSeverity.Critical      => ":red_circle:",
        DriftSeverity.Warning       => ":large_yellow_circle:",
        DriftSeverity.Informational => ":large_blue_circle:",
        _ => ":white_circle:"
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
