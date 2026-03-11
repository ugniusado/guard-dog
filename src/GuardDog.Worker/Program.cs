using GuardDog.Core.Extensions;
using GuardDog.Core.Metrics;
using GuardDog.Worker;
using Prometheus;

// ── Host setup ────────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// Bind GuardDog options and register all core services
builder.Services.AddGuardDog(builder.Configuration);

// Register the background worker
builder.Services.AddHostedService<Worker>();

// ── Prometheus metrics HTTP endpoint (Phase 5) ────────────────────────────────
// Exposes metrics at http://0.0.0.0:{MetricsPort}/metrics for scraping by
// Prometheus / Grafana.  Secure this endpoint in production using network
// policies or a reverse proxy — do NOT expose it publicly.
//
// In Azure: use Managed Identity for DB connectivity (no credentials in config).
// On AWS:   use IAM Role attached to the ECS Task / EC2 instance.
var metricsPort = builder.Configuration
    .GetSection("GuardDog")
    .GetValue<int?>("MetricsPort") ?? 9090;

var enableMetrics = builder.Configuration
    .GetSection("GuardDog")
    .GetValue<bool?>("EnablePrometheusMetrics") ?? true;

if (enableMetrics)
{
    var metricsServer = new MetricServer(port: metricsPort);
    metricsServer.Start();

    // Pre-create all metrics so they appear in Prometheus even before the first check
    _ = GuardDogMetrics.Instance;
}

var host = builder.Build();
host.Run();
