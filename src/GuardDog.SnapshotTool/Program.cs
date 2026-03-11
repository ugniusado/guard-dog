/**
 * GuardDog.SnapshotTool — Phase 2: Shadow Schema Generator
 *
 * PURPOSE
 * -------
 * This CLI tool is run during your CI/CD pipeline immediately after the publish step.
 * It loads a DbContext from a published assembly using a proper AssemblyLoadContext
 * (so all transitive dependencies resolve via the .deps.json file), calls
 * EfCoreSnapshotGenerator to introspect the EF Core model, and writes a snapshot JSON.
 *
 * USAGE
 * -----
 *   dotnet run --project src/GuardDog.SnapshotTool -- \
 *       --assembly    ./publish/app/MyApp.dll \
 *       --context     MyApp.Data.AppDbContext \
 *       --output      ./snapshot.json \
 *       --version     ${{ github.sha }}
 *
 * KEY DESIGN: AssemblyLoadContext + AssemblyDependencyResolver
 * ------------------------------------------------------------
 * Assembly.LoadFrom() uses the DEFAULT load context, which only looks in the
 * current directory and GAC — it cannot find the target app's transitive
 * dependencies (e.g. Microsoft.Extensions.Hosting.Abstractions).
 *
 * AssemblyDependencyResolver reads the <assembly>.deps.json produced by
 * `dotnet publish`, giving it the full dependency graph. Paired with a custom
 * AssemblyLoadContext, this resolves every dependency from the publish folder.
 */

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuardDog.Core.Models;
using GuardDog.Core.Snapshot;
using Microsoft.EntityFrameworkCore;

// ── Parse arguments ───────────────────────────────────────────────────────────

var argsMap = ParseArgs(args);

var assemblyPath    = GetArg("--assembly");
var contextTypeName = GetArg("--context");
var outputPath      = GetArg("--output",  "snapshot.json");
var version         = GetArg("--version", "1.0.0");

Console.WriteLine("Guard Dog Snapshot Tool");
Console.WriteLine("=======================");
Console.WriteLine($"Assembly : {assemblyPath}");
Console.WriteLine($"Context  : {contextTypeName}");
Console.WriteLine($"Output   : {outputPath}");
Console.WriteLine($"Version  : {version}");
Console.WriteLine();

// ── Load the DbContext via a proper AssemblyLoadContext ───────────────────────

DbContext context;
try
{
    var fullPath   = Path.GetFullPath(assemblyPath);
    var loadContext = new TargetAssemblyLoadContext(fullPath);
    var assembly   = loadContext.LoadFromAssemblyPath(fullPath);

    var contextType = assembly.GetType(contextTypeName)
        ?? throw new InvalidOperationException(
            $"Type '{contextTypeName}' not found in '{assemblyPath}'.\n" +
            "Available DbContext types:\n  " +
            string.Join("\n  ", assembly.GetTypes()
                .Where(t => typeof(DbContext).IsAssignableFrom(t) && !t.IsAbstract)
                .Select(t => t.FullName)));

    context = CreateContext(contextType);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR loading DbContext: {ex.Message}");
    if (ex.InnerException is not null)
        Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
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
        WriteIndented            = true,
        DefaultIgnoreCondition   = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy     = JsonNamingPolicy.CamelCase
    });

    await File.WriteAllTextAsync(outputPath, json);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR writing snapshot to '{outputPath}': {ex.Message}");
    return 1;
}

// ── Summary ───────────────────────────────────────────────────────────────────

Console.WriteLine("Snapshot generated successfully.");
Console.WriteLine($"  Tables    : {snapshot.Tables.Count}");
Console.WriteLine($"  Columns   : {snapshot.Tables.Sum(t => t.Columns.Count)}");
Console.WriteLine($"  Indexes   : {snapshot.Tables.Sum(t => t.Indexes.Count)}");
Console.WriteLine($"  FKs       : {snapshot.Tables.Sum(t => t.ForeignKeys.Count)}");
Console.WriteLine($"  ModelHash : {snapshot.ModelHash}");
Console.WriteLine($"  Output    : {Path.GetFullPath(outputPath)}");

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
        var factory      = Activator.CreateInstance(factoryType)!;
        var createMethod = factoryType.GetMethod("CreateDbContext")!;
        return (DbContext)createMethod.Invoke(factory, [Array.Empty<string>()])!;
    }

    // 2. Fall back: DbContextOptionsBuilder with InMemory provider
    //    We only need the EF Model metadata — no real DB connection required.
    var optionsBuilderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
    var optionsBuilder     = (DbContextOptionsBuilder)Activator.CreateInstance(optionsBuilderType)!;

    Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions
        .UseInMemoryDatabase(optionsBuilder, "SnapshotToolDesignTime");

    var ctor = contextType.GetConstructor([optionsBuilder.Options.GetType()])
           ?? contextType.GetConstructor([typeof(DbContextOptions)]);

    return ctor is not null
        ? (DbContext)ctor.Invoke([optionsBuilder.Options])
        : (DbContext)Activator.CreateInstance(contextType)!;
}

static Dictionary<string, string> ParseArgs(string[] rawArgs)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < rawArgs.Length - 1; i++)
        if (rawArgs[i].StartsWith("--"))
            map[rawArgs[i]] = rawArgs[i + 1];
    return map;
}

string GetArg(string name, string? defaultValue = null)
{
    if (argsMap.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val))
        return val;
    if (defaultValue is not null)
        return defaultValue;

    Console.Error.WriteLine($"ERROR: Required argument '{name}' is missing.");
    Environment.Exit(1);
    return null!;
}

// ── Custom AssemblyLoadContext ─────────────────────────────────────────────────

/// <summary>
/// Loads an assembly and resolves all its dependencies using the
/// <see cref="AssemblyDependencyResolver"/>, which reads the
/// <c>&lt;assembly&gt;.deps.json</c> produced by <c>dotnet publish</c>.
///
/// This is the correct way to load "foreign" assemblies at runtime in .NET —
/// <c>Assembly.LoadFrom()</c> uses the default load context and cannot resolve
/// transitive dependencies that live in the target's publish folder.
/// </summary>
sealed class TargetAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public TargetAssemblyLoadContext(string assemblyPath)
        : base(name: "GuardDogTarget", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Ask the resolver where this dependency lives in the publish folder.
        // If it can't find it, return null → fallback to the default context
        // (handles shared framework assemblies like System.Runtime).
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
