using System.Text.Json;
using System.Text.Json.Serialization;
using GuardDog.Core.Extensions;
using GuardDog.Core.Metrics;
using GuardDog.Core.Snapshot;
using GuardDog.Worker;
using Microsoft.EntityFrameworkCore;
using Prometheus;

// ── Snapshot generation mode (Phase 2 — CI/CD integration) ───────────────────
// When called with `--generate-snapshot <path>` the process generates a
// schema snapshot JSON from SampleDbContext and exits immediately.
// This runs under the Worker's own runtimeconfig so every dependency resolves
// correctly — no external assembly loading or reflection gymnastics required.
//
// CI/CD usage:
//   dotnet publish src/GuardDog.Worker -c Release -o ./publish/app
//   dotnet ./publish/app/GuardDog.Worker.dll \
//       --generate-snapshot ./snapshot.json \
//       --snapshot-version  "$GITHUB_SHA"
if (args.Contains("--generate-snapshot"))
{
    var outputIndex  = Array.IndexOf(args, "--generate-snapshot");
    var outputPath   = outputIndex < args.Length - 1 ? args[outputIndex + 1] : "snapshot.json";

    var versionIndex = Array.IndexOf(args, "--snapshot-version");
    var version      = versionIndex >= 0 && versionIndex < args.Length - 1
        ? args[versionIndex + 1]
        : "1.0.0";

    Console.WriteLine($"Guard Dog — generating snapshot v{version} → {outputPath}");

    var optionsBuilder = new DbContextOptionsBuilder<SampleDbContext>();
    optionsBuilder.UseInMemoryDatabase("SnapshotGeneration");

    await using var context = new SampleDbContext(optionsBuilder.Options);

    var snapshot = new EfCoreSnapshotGenerator().Generate(context, version);

    var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase
    }));

    Console.WriteLine($"  Tables : {snapshot.Tables.Count}");
    Console.WriteLine($"  Hash   : {snapshot.ModelHash}");
    Console.WriteLine("Done.");
    return 0;
}

// ── Normal Worker Service mode ────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddGuardDog(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var metricsPort = builder.Configuration
    .GetSection("GuardDog")
    .GetValue<int?>("MetricsPort") ?? 9090;

var enableMetrics = builder.Configuration
    .GetSection("GuardDog")
    .GetValue<bool?>("EnablePrometheusMetrics") ?? true;

if (enableMetrics)
{
    new MetricServer(port: metricsPort).Start();
    _ = GuardDogMetrics.Instance;
}

var host = builder.Build();
host.Run();
return 0;
