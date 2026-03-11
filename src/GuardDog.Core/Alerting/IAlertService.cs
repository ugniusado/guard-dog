using GuardDog.Core.Models;

namespace GuardDog.Core.Alerting;

/// <summary>
/// Sends a drift notification to an external channel (Slack, Teams, etc.).
/// </summary>
public interface IAlertService
{
    Task SendAsync(DriftReport report, CancellationToken ct = default);
}
