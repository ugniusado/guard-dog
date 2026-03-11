using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace GuardDog.Core.Models;

// ── Root snapshot ─────────────────────────────────────────────────────────────

/// <summary>
/// A point-in-time capture of the EF Core model schema, serialised to JSON by
/// the GuardDog.SnapshotTool during CI/CD and embedded into the worker service
/// as a build artifact. This is the "Shadow Schema" — the authoritative record
/// of what the deployed code expects the database to look like.
/// </summary>
public sealed record SchemaSnapshot
{
    public required string Version       { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string Provider      { get; init; }  // e.g. SqlServer, PostgreSQL
    public required string ModelHash     { get; init; }  // SHA-256 of the sorted schema
    public required IReadOnlyList<TableSnapshot> Tables { get; init; }

    [JsonIgnore]
    public static SchemaSnapshot Empty => new()
    {
        Version     = "0.0.0",
        GeneratedAt = DateTimeOffset.MinValue,
        Provider    = "Unknown",
        ModelHash   = string.Empty,
        Tables      = []
    };

    /// <summary>Compute a deterministic hash over the table/column definitions.</summary>
    public static string ComputeHash(IEnumerable<TableSnapshot> tables)
    {
        var sorted = tables
            .OrderBy(t => t.Schema)
            .ThenBy(t => t.Name)
            .Select(t => $"{t.Schema}.{t.Name}:" +
                string.Join(",", t.Columns
                    .OrderBy(c => c.Name)
                    .Select(c => $"{c.Name}:{c.StoreType}:{c.IsNullable}")));

        var raw = string.Join("|", sorted);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

// ── Table ─────────────────────────────────────────────────────────────────────

public sealed record TableSnapshot
{
    public required string Schema { get; init; }
    public required string Name   { get; init; }
    public required IReadOnlyList<ColumnSnapshot>     Columns     { get; init; }
    public required IReadOnlyList<IndexSnapshot>      Indexes     { get; init; }
    public required IReadOnlyList<ForeignKeySnapshot> ForeignKeys { get; init; }
    public required string?                           PrimaryKeyName    { get; init; }
    public required IReadOnlyList<string>             PrimaryKeyColumns { get; init; }
}

// ── Column ───────────────────────────────────────────────────────────────────

public sealed record ColumnSnapshot
{
    public required string  Name           { get; init; }
    public required string  StoreType      { get; init; }
    public required bool    IsNullable     { get; init; }
    public required bool    IsIdentity     { get; init; }
    public          int?    MaxLength      { get; init; }
    public          string? DefaultValueSql { get; init; }
}

// ── Index ────────────────────────────────────────────────────────────────────

public sealed record IndexSnapshot
{
    public required string                Name    { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required bool                  IsUnique { get; init; }
}

// ── Foreign Key ──────────────────────────────────────────────────────────────

public sealed record ForeignKeySnapshot
{
    public required string                Name             { get; init; }
    public required IReadOnlyList<string> Columns          { get; init; }
    public required string                PrincipalSchema  { get; init; }
    public required string                PrincipalTable   { get; init; }
    public required IReadOnlyList<string> PrincipalColumns { get; init; }
    public required string                OnDelete         { get; init; }
}
