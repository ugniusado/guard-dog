/**
 * GuardDog.SnapshotTool — Phase 2: Shadow Schema Generator
 *
 * PURPOSE
 * -------
 * This CLI tool is run during your CI/CD pipeline immediately after the build step.
 * It loads a DbContext from a compiled assembly, calls EfCoreSnapshotGenerator to
 * introspect the EF Core model, and writes a schema snapshot JSON file.
 *
 * That snapshot is then embedded into GuardDog.Worker as a build artifact so the
 * worker can compare the expected DB schema (at deploy time) against the live DB
 * without needing the application source code at runtime.
 *
 * USAGE
 * -----
 *   dotnet run --project src/GuardDog.SnapshotTool -- \
 *       --assembly    ./MyApp/bin/Release/net10.0/MyApp.dll \
 *       --context     MyApp.Data.AppDbContext \
 *       --output      ./snapshot.json \
 *       --version     ${{ github.sha }}
 *
 * ARGUMENTS
 * ---------
 *   --assembly  Path to the compiled application assembly containing the DbContext.
 *   --context   Fully qualified name of the DbContext type to introspect.
 *   --output    Path for the generated snapshot.json file (default: snapshot.json).
 *   --version   Snapshot version string (default: 1.0.0; use git SHA in CI).
 *   --provider  EF provider name used for default schema inference
 *               (SqlServer | PostgreSQL; default: SqlServer).
 *
 * SECURITY NOTE
 * -------------
 * The snapshot tool only reads the EF model metadata — it does NOT connect to
 * any database.  It is safe to run in CI without DB credentials.
 */

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuardDog.Core.Models;
using GuardDog.Core.Snapshot;
using Microsoft.EntityFrameworkCore;

// ── Parse arguments ───────────────────────────────────────────────────────────

var args_map = ParseArgs(args);

var assemblyPath = GetArg("--assembly");
var contextTypeName = GetArg("--context");
var outputPath = GetArg("--output", "snapshot.json");
var version = GetArg("--version", "1.0.0");
var provider = GetArg("--provider", "SqlServer");

Console.WriteLine("Guard Dog Snapshot Tool");
Console.WriteLine("=======================");
Console.WriteLine($"Assembly : {assemblyPath}");
Console.WriteLine($"Context  : {contextTypeName}");
Console.WriteLine($"Output   : {outputPath}");
Console.WriteLine($"Version  : {version}");
Console.WriteLine();

// ── Load the DbContext ────────────────────────────────────────────────────────

DbContext context;
try
{
    var assembly   = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
    var contextType = assembly.GetType(contextTypeName)
        ?? throw new InvalidOperationException(
            $"Type '{contextTypeName}' not found in assembly '{assemblyPath}'. " +
            $"Available DbContext types:\n  " +
            string.Join("\n  ", assembly.GetTypes()
                .Where(t => t.IsAssignableTo(typeof(DbContext)) && !t.IsAbstract)
                .Select(t => t.FullName)));

    // Create a design-time instance using the parameterless ctor or
    // IDesignTimeDbContextFactory<T> if present — for snapshot generation we
    // only need the Model, so an in-memory provider suffices.
    context = CreateContext(contextType);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR loading DbContext: {ex.Message}");
    return 1;
}

// ── Generate the snapshot ─────────────────────────────────────────────────────

SchemaSnapshot snapshot;
try
{
    var generator = new EfCoreSnapshotGenerator();
    snapshot = generator.Generate(context, version);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR generating snapshot: {ex.Message}");
    return 1;
}

// ── Write to disk ─────────────────────────────────────────────────────────────

try
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(dir))
        Directory.CreateDirectory(dir);

    var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
    {
        WriteIndented       = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });

    await File.WriteAllTextAsync(outputPath, json);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR writing snapshot to '{outputPath}': {ex.Message}");
    return 1;
}

// ── Summary ───────────────────────────────────────────────────────────────────

Console.WriteLine($"Snapshot generated successfully.");
Console.WriteLine($"  Tables    : {snapshot.Tables.Count}");
Console.WriteLine($"  Columns   : {snapshot.Tables.Sum(t => t.Columns.Count)}");
Console.WriteLine($"  Indexes   : {snapshot.Tables.Sum(t => t.Indexes.Count)}");
Console.WriteLine($"  FKs       : {snapshot.Tables.Sum(t => t.ForeignKeys.Count)}");
Console.WriteLine($"  ModelHash : {snapshot.ModelHash}");
Console.WriteLine($"  Output    : {Path.GetFullPath(outputPath)}");
Console.WriteLine();
Console.WriteLine("Next step: embed snapshot.json into GuardDog.Worker as an EmbeddedResource,");
Console.WriteLine("or mount it as a ConfigMap/volume in your deployment.");

return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────

static DbContext CreateContext(Type contextType)
{
    // 1. Try IDesignTimeDbContextFactory<T>
    var factoryInterface = typeof(Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<>)
        .MakeGenericType(contextType);

    var factoryType = contextType.Assembly.GetTypes()
        .FirstOrDefault(t => t.IsAssignableTo(factoryInterface) && !t.IsAbstract);

    if (factoryType is not null)
    {
        var factory = Activator.CreateInstance(factoryType)!;
        var createMethod = factoryType.GetMethod("CreateDbContext")!;
        return (DbContext)createMethod.Invoke(factory, [Array.Empty<string>()])!;
    }

    // 2. Fall back to DbContextOptionsBuilder with InMemory provider
    var optionsBuilderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
    var optionsBuilder     = (DbContextOptionsBuilder)Activator.CreateInstance(optionsBuilderType)!;

    // Use InMemory so we get the EF model without a real DB connection
    Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions
        .UseInMemoryDatabase(optionsBuilder, "SnapshotToolDesignTime");

    var ctor = contextType.GetConstructor([optionsBuilder.Options.GetType()])
           ?? contextType.GetConstructor([typeof(DbContextOptions)]);

    return ctor is not null
        ? (DbContext)ctor.Invoke([optionsBuilder.Options])
        : (DbContext)Activator.CreateInstance(contextType)!;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].StartsWith("--"))
            map[args[i]] = args[i + 1];
    return map;
}

string GetArg(string name, string? defaultValue = null)
{
    if (args_map.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val))
        return val;
    if (defaultValue is not null)
        return defaultValue;

    Console.Error.WriteLine($"ERROR: Required argument '{name}' is missing.");
    Environment.Exit(1);
    return null!;
}
