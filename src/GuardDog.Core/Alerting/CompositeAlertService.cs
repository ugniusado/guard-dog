using GuardDog.Core.Models;
using Microsoft.Extensions.Logging;

namespace GuardDog.Core.Alerting;

/// <summary>
/// Fan-out implementation: dispatches the drift report to every registered
/// <see cref="IAlertService"/> in parallel, collecting failures without
/// propagating them so one broken webhook cannot silence the others.
/// </summary>
public sealed class CompositeAlertService : IAlertService
{
    private readonly IEnumerable<IAlertService> _services;
    private readonly ILogger<CompositeAlertService> _logger;

    public CompositeAlertService(
        IEnumerable<IAlertService> services,
        ILogger<CompositeAlertService> logger)
    {
        _services = services;
        _logger   = logger;
    }

    public async Task SendAsync(DriftReport report, CancellationToken ct = default)
    {
        var tasks = _services
            .Select(async svc =>
            {
                try   { await svc.SendAsync(report, ct); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Alert service {Service} failed to send notification.",
                        svc.GetType().Name);
                }
            });

        await Task.WhenAll(tasks);
    }
}
