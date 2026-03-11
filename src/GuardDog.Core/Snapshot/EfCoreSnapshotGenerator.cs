using GuardDog.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GuardDog.Core.Snapshot;

/// <summary>
/// Introspects the EF Core <see cref="IModel"/> at design-time / application
/// startup using the public metadata API and produces a <see cref="SchemaSnapshot"/>
/// that is serialised to JSON for embedding in the deployed worker.
///
/// Key EF Core APIs used:
///   • context.Model.GetEntityTypes()         → all mapped entity types
///   • entityType.GetTableName() / GetSchema() → relational table name
///   • property.GetColumnName(StoreObjectIdentifier) → actual DB column name
///   • property.GetColumnType()               → provider-specific store type
///   • entityType.GetIndexes()                → index metadata
///   • entityType.GetForeignKeys()            → FK metadata
/// </summary>
public sealed class EfCoreSnapshotGenerator : ISnapshotGenerator
{
    public SchemaSnapshot Generate(DbContext context, string version = "1.0.0")
    {
        var provider = context.Database.ProviderName ?? "Unknown";
        var tables   = new List<TableSnapshot>();

        // Only process root entity types (skip owned entity types that share a table
        // with their owner — they appear as columns, not separate tables)
        var entityTypes = context.Model
            .GetEntityTypes()
            .Where(e => e.GetTableName() is not null
                     && !e.IsOwned()
                     && e.BaseType is null)   // skip hierarchy derived types
            .ToList();

        foreach (var entityType in entityTypes)
        {
            var tableName = entityType.GetTableName()!;
            var schema    = entityType.GetSchema() ?? GetDefaultSchema(provider);
            var storeObj  = StoreObjectIdentifier.Table(tableName, schema);

            // ── Columns ────────────────────────────────────────────────────────
            // GetProperties() returns the entity's own properties.
            // We must also include properties from owned types that are inlined.
            var columns = GetAllProperties(entityType)
                .Where(p => p.GetColumnName(storeObj) is not null)
                .Select(p =>
                {
                    var colName = p.GetColumnName(storeObj)!;
                    return new ColumnSnapshot
                    {
                        Name           = colName,
                        StoreType      = p.GetColumnType(),
                        IsNullable     = p.IsNullable,
                        IsIdentity     = p.ValueGenerated == ValueGenerated.OnAdd
                                      && !p.IsNullable,
                        MaxLength      = p.GetMaxLength(),
                        DefaultValueSql = p.GetDefaultValueSql()
                    };
                })
                .DistinctBy(c => c.Name.ToLowerInvariant())
                .ToList();

            // ── Primary Key ────────────────────────────────────────────────────
            var pk       = entityType.FindPrimaryKey();
            var pkName   = pk?.GetName();
            var pkCols   = pk?.Properties
                .Select(p => p.GetColumnName(storeObj) ?? p.Name)
                .ToList() ?? [];

            // ── Indexes ────────────────────────────────────────────────────────
            var indexes = entityType.GetIndexes()
                .Where(i => i.GetDatabaseName() is not null)
                .Select(i => new IndexSnapshot
                {
                    Name     = i.GetDatabaseName()!,
                    IsUnique = i.IsUnique,
                    Columns  = i.Properties
                        .Select(p => p.GetColumnName(storeObj) ?? p.Name)
                        .ToList()
                })
                .ToList();

            // ── Foreign Keys ───────────────────────────────────────────────────
            var foreignKeys = entityType.GetForeignKeys()
                .Where(fk => fk.GetConstraintName() is not null
                          && fk.PrincipalEntityType.GetTableName() is not null)
                .Select(fk =>
                {
                    var principalTableName = fk.PrincipalEntityType.GetTableName()!;
                    var principalSchema    = fk.PrincipalEntityType.GetSchema()
                                         ?? GetDefaultSchema(provider);
                    var principalStore     = StoreObjectIdentifier.Table(principalTableName, principalSchema);

                    return new ForeignKeySnapshot
                    {
                        Name            = fk.GetConstraintName()!,
                        Columns         = fk.Properties
                            .Select(p => p.GetColumnName(storeObj) ?? p.Name)
                            .ToList(),
                        PrincipalSchema  = principalSchema,
                        PrincipalTable   = principalTableName,
                        PrincipalColumns = fk.PrincipalKey.Properties
                            .Select(p => p.GetColumnName(principalStore) ?? p.Name)
                            .ToList(),
                        OnDelete = fk.DeleteBehavior.ToString().ToUpperInvariant()
                    };
                })
                .ToList();

            tables.Add(new TableSnapshot
            {
                Schema      = schema,
                Name        = tableName,
                Columns     = columns,
                Indexes     = indexes,
                ForeignKeys = foreignKeys,
                PrimaryKeyName    = pkName,
                PrimaryKeyColumns = pkCols
            });
        }

        var hash = SchemaSnapshot.ComputeHash(tables);

        return new SchemaSnapshot
        {
            Version     = version,
            GeneratedAt = DateTimeOffset.UtcNow,
            Provider    = SimplifyProvider(provider),
            ModelHash   = hash,
            Tables      = tables
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Flattens owned entity types (TPO / table-splitting) into the parent entity's
    /// property list so all columns appear in one table.
    /// </summary>
    private static IEnumerable<IProperty> GetAllProperties(IEntityType entityType)
    {
        foreach (var p in entityType.GetProperties())
            yield return p;

        foreach (var owned in entityType.GetNavigations()
                     .Where(n => n.TargetEntityType.IsOwned()))
        {
            foreach (var p in GetAllProperties(owned.TargetEntityType))
                yield return p;
        }
    }

    private static string GetDefaultSchema(string? provider) =>
        provider switch
        {
            _ when provider?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true => "dbo",
            _ when provider?.Contains("Npgsql",    StringComparison.OrdinalIgnoreCase) == true => "public",
            _ => "dbo"
        };

    private static string SimplifyProvider(string? provider) =>
        provider switch
        {
            _ when provider?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true => "SqlServer",
            _ when provider?.Contains("Npgsql",    StringComparison.OrdinalIgnoreCase) == true => "PostgreSQL",
            _ when provider?.Contains("MySql",     StringComparison.OrdinalIgnoreCase) == true => "MySQL",
            _ => provider ?? "Unknown"
        };
}
