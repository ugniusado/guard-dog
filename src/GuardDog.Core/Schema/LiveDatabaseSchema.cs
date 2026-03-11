namespace GuardDog.Core.Schema;

// ── Live database object model ────────────────────────────────────────────────
// These types mirror SchemaSnapshot but are populated by reading the live DB,
// keeping concerns cleanly separated.

public sealed record LiveSchema(
    IReadOnlyList<LiveTable> Tables);

public sealed record LiveTable(
    string Schema,
    string Name,
    string? PrimaryKeyName,
    IReadOnlyList<string>    PrimaryKeyColumns,
    IReadOnlyList<LiveColumn> Columns,
    IReadOnlyList<LiveIndex>  Indexes,
    IReadOnlyList<LiveForeignKey> ForeignKeys);

public sealed record LiveColumn(
    string  Name,
    string  StoreType,
    bool    IsNullable,
    string? DefaultValueSql,
    int?    MaxLength);

public sealed record LiveIndex(
    string               Name,
    IReadOnlyList<string> Columns,
    bool                 IsUnique);

public sealed record LiveForeignKey(
    string               Name,
    IReadOnlyList<string> Columns,
    string               PrincipalSchema,
    string               PrincipalTable,
    IReadOnlyList<string> PrincipalColumns,
    string               OnDelete);
