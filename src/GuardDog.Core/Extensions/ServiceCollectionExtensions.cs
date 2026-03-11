using GuardDog.Core.Alerting;
using GuardDog.Core.Diagram;
using GuardDog.Core.DriftEngine;
using GuardDog.Core.Models;
using GuardDog.Core.Schema;
using GuardDog.Core.Snapshot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GuardDog.Core.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Guard Dog core services.
    ///
    /// Typical usage in Program.cs:
    /// <code>
    /// builder.Services.AddGuardDog(builder.Configuration);
    /// </code>
    /// </summary>
    public static IServiceCollection AddGuardDog(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<GuardDogOptions>(
            configuration.GetSection(GuardDogOptions.SectionName));

        // Core detection pipeline
        services.AddSingleton<IDriftDetector, DriftDetector>();
        services.AddSingleton<ISnapshotGenerator, EfCoreSnapshotGenerator>();
        services.AddSingleton<IMermaidGenerator, MermaidErDiagramGenerator>();

        // Schema readers are stateless; factory produces the correct one at runtime
        services.AddSingleton<IDatabaseSchemaReader>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<GuardDogOptions>>().Value;
            return DatabaseSchemaReaderFactory.Create(opts.Provider);
        });

        // Alerting
        services.AddHttpClient<SlackAlertService>();
        services.AddHttpClient<TeamsAlertService>();

        // Register concrete services AND composite fan-out
        services.AddSingleton<SlackAlertService>();
        services.AddSingleton<TeamsAlertService>();
        services.AddSingleton<IAlertService>(sp =>
        {
            var opts    = sp.GetRequiredService<IOptions<GuardDogOptions>>().Value;
            var alerters = new List<IAlertService>();

            if (opts.Slack?.WebhookUrl is { Length: > 0 })
                alerters.Add(sp.GetRequiredService<SlackAlertService>());

            if (opts.Teams?.WebhookUrl is { Length: > 0 })
                alerters.Add(sp.GetRequiredService<TeamsAlertService>());

            return new CompositeAlertService(
                alerters,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CompositeAlertService>>());
        });

        return services;
    }
}
