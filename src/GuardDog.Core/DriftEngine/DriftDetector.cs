using GuardDog.Core.Models;
using GuardDog.Core.Schema;

namespace GuardDog.Core.DriftEngine;

/// <summary>
/// Core drift detection engine.
///
/// Severity classification rules:
///   Critical     – The application will throw at runtime (missing table/column,
///                  wrong column type).
///   Warning      – The application may fail under specific conditions (nullability
///                  mismatch, missing FK constraint).
///   Informational – Extra objects that exist in the DB but are unknown to the code
///                  model (extra index, extra column, extra table).
/// </summary>
public sealed class DriftDetector : IDriftDetector
{
    public DriftReport Detect(
        SchemaSnapshot snapshot,
        LiveSchema liveSchema,
        string provider,
        string dataSource)
    {
        var items = new List<DriftItem>();

        var liveTables = liveSchema.Tables
            .ToDictionary(t => ($"{t.Schema}.{t.Name}").ToLowerInvariant(), t => t);

        // ── 1. Walk every table the code model knows about ────────────────────
        foreach (var codeTable in snapshot.Tables)
        {
            var key = $"{codeTable.Schema}.{codeTable.Name}".ToLowerInvariant();

            if (!liveTables.TryGetValue(key, out var dbTable))
            {
                // The entire table is missing from the DB
                items.Add(new DriftItem
                {
                    TableSchema  = codeTable.Schema,
                    TableName    = codeTable.Name,
                    ObjectName   = codeTable.Name,
                    Kind         = DriftKind.MissingTable,
                    Severity     = DriftSeverity.Critical,
                    CodeState    = "exists",
                    DatabaseState = "missing",
                    FixScript    = GenerateCreateTableScript(codeTable, provider)
                });
                continue;
            }

            // ── 2. Primary Key ─────────────────────────────────────────────────
            ComparePrimaryKeys(codeTable, dbTable, items, provider);

            // ── 3. Columns ─────────────────────────────────────────────────────
            CompareColumns(codeTable, dbTable, items, provider);

            // ── 4. Indexes ─────────────────────────────────────────────────────
            CompareIndexes(codeTable, dbTable, items, provider);

            // ── 5. Foreign Keys ────────────────────────────────────────────────
            CompareForeignKeys(codeTable, dbTable, items, provider);
        }

        // ── 6. Extra tables in DB not in code model (informational) ───────────
        var codeTableKeys = snapshot.Tables
            .Select(t => $"{t.Schema}.{t.Name}".ToLowerInvariant())
            .ToHashSet();

        foreach (var dbTable in liveSchema.Tables)
        {
            var key = $"{dbTable.Schema}.{dbTable.Name}".ToLowerInvariant();
            if (!codeTableKeys.Contains(key))
                items.Add(new DriftItem
                {
                    TableSchema   = dbTable.Schema,
                    TableName     = dbTable.Name,
                    ObjectName    = dbTable.Name,
                    Kind          = DriftKind.ExtraTable,
                    Severity      = DriftSeverity.Informational,
                    CodeState     = "not mapped",
                    DatabaseState = "exists",
                    FixScript     = null
                });
        }

        return new DriftReport
        {
            CheckedAt       = DateTimeOffset.UtcNow,
            DatabaseProvider = provider,
            DataSource      = dataSource,
            SnapshotVersion = snapshot.Version,
            Items           = items.AsReadOnly()
        };
    }

    // ── Primary Key comparison ────────────────────────────────────────────────

    private static void ComparePrimaryKeys(
        TableSnapshot codeTable,
        LiveTable dbTable,
        List<DriftItem> items,
        string provider)
    {
        var codeColsCsv = string.Join(",", codeTable.PrimaryKeyColumns.OrderBy(c => c));
        var dbColsCsv   = string.Join(",", dbTable.PrimaryKeyColumns.OrderBy(c => c));

        if (!string.Equals(codeColsCsv, dbColsCsv, StringComparison.OrdinalIgnoreCase))
            items.Add(new DriftItem
            {
                TableSchema   = codeTable.Schema,
                TableName     = codeTable.Name,
                ObjectName    = codeTable.PrimaryKeyName ?? "PRIMARY KEY",
                Kind          = DriftKind.PrimaryKeyMismatch,
                Severity      = DriftSeverity.Critical,
                CodeState     = $"columns=({codeColsCsv})",
                DatabaseState = $"columns=({dbColsCsv})",
                FixScript     = null   // PK changes are risky; flag for manual review
            });
    }

    // ── Column comparison ─────────────────────────────────────────────────────

    private static void CompareColumns(
        TableSnapshot codeTable,
        LiveTable dbTable,
        List<DriftItem> items,
        string provider)
    {
        var dbCols = dbTable.Columns
            .ToDictionary(c => c.Name.ToLowerInvariant(), c => c);

        // Columns the code expects
        foreach (var codeCol in codeTable.Columns)
        {
            if (!dbCols.TryGetValue(codeCol.Name.ToLowerInvariant(), out var dbCol))
            {
                items.Add(new DriftItem
                {
                    TableSchema   = codeTable.Schema,
                    TableName     = codeTable.Name,
                    ObjectName    = codeCol.Name,
                    Kind          = DriftKind.MissingColumn,
                    Severity      = DriftSeverity.Critical,
                    CodeState     = $"{codeCol.StoreType} {(codeCol.IsNullable ? "NULL" : "NOT NULL")}",
                    DatabaseState = "column does not exist",
                    FixScript     = GenerateAddColumnScript(codeTable, codeCol, provider)
                });
                continue;
            }

            // Type mismatch
            if (!NormalizeType(codeCol.StoreType).Equals(NormalizeType(dbCol.StoreType),
                    StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new DriftItem
                {
                    TableSchema   = codeTable.Schema,
                    TableName     = codeTable.Name,
                    ObjectName    = codeCol.Name,
                    Kind          = DriftKind.ColumnTypeMismatch,
                    Severity      = DriftSeverity.Critical,
                    CodeState     = codeCol.StoreType,
                    DatabaseState = dbCol.StoreType,
                    FixScript     = GenerateAlterColumnScript(codeTable, codeCol, provider)
                });
            }

            // Nullability mismatch
            if (codeCol.IsNullable != dbCol.IsNullable)
            {
                items.Add(new DriftItem
                {
                    TableSchema   = codeTable.Schema,
                    TableName     = codeTable.Name,
                    ObjectName    = codeCol.Name,
                    Kind          = DriftKind.NullabilityMismatch,
                    Severity      = DriftSeverity.Warning,
                    CodeState     = codeCol.IsNullable ? "NULL" : "NOT NULL",
                    DatabaseState = dbCol.IsNullable   ? "NULL" : "NOT NULL",
                    FixScript     = GenerateAlterColumnScript(codeTable, codeCol, provider)
                });
            }
        }

        // Extra columns in DB not in code model
        var codeColNames = codeTable.Columns
            .Select(c => c.Name.ToLowerInvariant())
            .ToHashSet();

        foreach (var dbCol in dbTable.Columns)
        {
            if (!codeColNames.Contains(dbCol.Name.ToLowerInvariant()))
                items.Add(new DriftItem
                {
                    TableSchema   = codeTable.Schema,
                    TableName     = codeTable.Name,
                    ObjectName    = dbCol.Name,
                    Kind          = DriftKind.ExtraColumn,
                    Severity      = DriftSeverity.Informational,
                    CodeState     = "not mapped in code",
                    DatabaseState = $"{dbCol.StoreType} {(dbCol.IsNullable ? "NULL" : "NOT NULL")}",
                    FixScript     = null
                });
        }
    }

    // ── Index comparison ──────────────────────────────────────────────────────

    private static void CompareIndexes(
        TableSnapshot codeTable,
        LiveTable dbTable,
        List<DriftItem> items,
        string provider)
    {
        var dbIndexes = dbTable.Indexes
            .ToDictionary(i => i.Name.ToLowerInvariant(), i => i);

        foreach (var codeIdx in codeTable.Indexes)
        {
            if (!dbIndexes.ContainsKey(codeIdx.Name.ToLowerInvariant()))
                items.Add(new DriftItem
                {
                    TableSchema   = codeTable.Schema,
                    TableName     = codeTable.Name,
                    ObjectName    = codeIdx.Name,
                    Kind          = DriftKind.MissingIndex,
                    Severity      = DriftSeverity.Informational,
                    CodeState     = $"index on ({string.Join(", ", codeIdx.Columns)})",
                    DatabaseState = "index does not exist",
                    FixScript     = GenerateCreateIndexScript(codeTable, codeIdx, provider)
                });
        }

        var codeIndexNames = codeTable.Indexes
            .Select(i => i.Name.ToLowerInvariant())
            .ToHashSet();

        foreach (var dbIdx in dbTable.Indexes)
        {
            if (!codeIndexNames.Contains(dbIdx.Name.ToLowerInvariant()))
                items.Add(new DriftItem
                {
                    TableSchema   = codeTable.Schema,
                    TableName     = codeTable.Name,
                    ObjectName    = dbIdx.Name,
                    Kind          = DriftKind.ExtraIndex,
                    Severity      = DriftSeverity.Informational,
                    CodeState     = "not in code model",
                    DatabaseState = $"index on ({string.Join(", ", dbIdx.Columns)})",
                    FixScript     = null
                });
        }
    }

    // ── Foreign Key comparison ────────────────────────────────────────────────

    private static void CompareForeignKeys(
        TableSnapshot codeTable,
        LiveTable dbTable,
        List<DriftItem> items,
        string provider)
    {
        var dbFks = dbTable.ForeignKeys
            .ToDictionary(f => f.Name.ToLowerInvariant(), f => f);

        foreach (var codeFk in codeTable.ForeignKeys)
        {
            if (!dbFks.ContainsKey(codeFk.Name.ToLowerInvariant()))
                items.Add(new DriftItem
                {
                    TableSchema   = codeTable.Schema,
                    TableName     = codeTable.Name,
                    ObjectName    = codeFk.Name,
                    Kind          = DriftKind.MissingForeignKey,
                    Severity      = DriftSeverity.Warning,
                    CodeState     = $"FK → {codeFk.PrincipalSchema}.{codeFk.PrincipalTable}",
                    DatabaseState = "constraint does not exist",
                    FixScript     = GenerateAddForeignKeyScript(codeTable, codeFk, provider)
                });
        }

        var codeFkNames = codeTable.ForeignKeys
            .Select(f => f.Name.ToLowerInvariant())
            .ToHashSet();

        foreach (var dbFk in dbTable.ForeignKeys)
        {
            if (!codeFkNames.Contains(dbFk.Name.ToLowerInvariant()))
                items.Add(new DriftItem
                {
                    TableSchema   = codeTable.Schema,
                    TableName     = codeTable.Name,
                    ObjectName    = dbFk.Name,
                    Kind          = DriftKind.ExtraForeignKey,
                    Severity      = DriftSeverity.Informational,
                    CodeState     = "not in code model",
                    DatabaseState = $"FK → {dbFk.PrincipalSchema}.{dbFk.PrincipalTable}",
                    FixScript     = null
                });
        }
    }

    // ── Fix-It Script Generation (Phase 1 "Senior Polish") ────────────────────

    private static string GenerateCreateTableScript(TableSnapshot table, string provider)
    {
        var cols = table.Columns
            .Select(c => FormatColumnDef(c, provider))
            .ToList();

        if (table.PrimaryKeyColumns.Count > 0)
        {
            var pkCols = string.Join(", ", table.PrimaryKeyColumns.Select(c => Quote(c, provider)));
            cols.Add($"CONSTRAINT {Quote(table.PrimaryKeyName ?? $"PK_{table.Name}", provider)} PRIMARY KEY ({pkCols})");
        }

        return provider.ToLowerInvariant() switch
        {
            "postgresql" or "postgres" =>
                $"CREATE TABLE {Quote(table.Schema, provider)}.{Quote(table.Name, provider)} (\n" +
                $"    {string.Join(",\n    ", cols)}\n);",
            _ =>
                $"CREATE TABLE [{table.Schema}].[{table.Name}] (\n" +
                $"    {string.Join(",\n    ", cols)}\n);"
        };
    }

    private static string GenerateAddColumnScript(TableSnapshot table, ColumnSnapshot col, string provider) =>
        provider.ToLowerInvariant() switch
        {
            "postgresql" or "postgres" =>
                $"ALTER TABLE {Quote(table.Schema, provider)}.{Quote(table.Name, provider)}\n" +
                $"    ADD COLUMN {FormatColumnDef(col, provider)};",
            _ =>
                $"ALTER TABLE [{table.Schema}].[{table.Name}]\n" +
                $"    ADD {FormatColumnDef(col, provider)};"
        };

    private static string GenerateAlterColumnScript(TableSnapshot table, ColumnSnapshot col, string provider) =>
        provider.ToLowerInvariant() switch
        {
            "postgresql" or "postgres" =>
                $"ALTER TABLE {Quote(table.Schema, provider)}.{Quote(table.Name, provider)}\n" +
                $"    ALTER COLUMN {Quote(col.Name, provider)} TYPE {col.StoreType},\n" +
                $"    ALTER COLUMN {Quote(col.Name, provider)} {(col.IsNullable ? "DROP NOT NULL" : "SET NOT NULL")};",
            _ =>
                $"ALTER TABLE [{table.Schema}].[{table.Name}]\n" +
                $"    ALTER COLUMN [{col.Name}] {col.StoreType} {(col.IsNullable ? "NULL" : "NOT NULL")};"
        };

    private static string GenerateCreateIndexScript(TableSnapshot table, IndexSnapshot idx, string provider)
    {
        var unique   = idx.IsUnique ? "UNIQUE " : string.Empty;
        var colsCsv  = string.Join(", ", idx.Columns.Select(c => Quote(c, provider)));
        return provider.ToLowerInvariant() switch
        {
            "postgresql" or "postgres" =>
                $"CREATE {unique}INDEX {Quote(idx.Name, provider)}\n" +
                $"    ON {Quote(table.Schema, provider)}.{Quote(table.Name, provider)} ({colsCsv});",
            _ =>
                $"CREATE {unique}INDEX [{idx.Name}]\n" +
                $"    ON [{table.Schema}].[{table.Name}] ({colsCsv});"
        };
    }

    private static string GenerateAddForeignKeyScript(TableSnapshot table, ForeignKeySnapshot fk, string provider)
    {
        var cols  = string.Join(", ", fk.Columns.Select(c => Quote(c, provider)));
        var pCols = string.Join(", ", fk.PrincipalColumns.Select(c => Quote(c, provider)));
        return provider.ToLowerInvariant() switch
        {
            "postgresql" or "postgres" =>
                $"ALTER TABLE {Quote(table.Schema, provider)}.{Quote(table.Name, provider)}\n" +
                $"    ADD CONSTRAINT {Quote(fk.Name, provider)}\n" +
                $"    FOREIGN KEY ({cols})\n" +
                $"    REFERENCES {Quote(fk.PrincipalSchema, provider)}.{Quote(fk.PrincipalTable, provider)} ({pCols})\n" +
                $"    ON DELETE {fk.OnDelete};",
            _ =>
                $"ALTER TABLE [{table.Schema}].[{table.Name}]\n" +
                $"    ADD CONSTRAINT [{fk.Name}]\n" +
                $"    FOREIGN KEY ({cols})\n" +
                $"    REFERENCES [{fk.PrincipalSchema}].[{fk.PrincipalTable}] ({pCols})\n" +
                $"    ON DELETE {fk.OnDelete};"
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatColumnDef(ColumnSnapshot col, string provider)
    {
        var nullable = col.IsNullable ? "NULL" : "NOT NULL";
        var identity = col.IsIdentity
            ? provider.ToLowerInvariant() switch
            {
                "postgresql" or "postgres" => " GENERATED ALWAYS AS IDENTITY",
                _ => " IDENTITY(1,1)"
            }
            : string.Empty;
        var def = col.DefaultValueSql is not null ? $" DEFAULT {col.DefaultValueSql}" : string.Empty;
        return $"{Quote(col.Name, provider)} {col.StoreType}{identity} {nullable}{def}";
    }

    private static string Quote(string name, string provider) =>
        provider.ToLowerInvariant() switch
        {
            "postgresql" or "postgres" => $"\"{name}\"",
            _ => $"[{name}]"
        };

    private static string NormalizeType(string type) =>
        type.ToLowerInvariant()
            .Replace("character varying", "varchar")
            .Replace("integer", "int")
            .Replace("boolean", "bool")
            .Replace("double precision", "float8")
            .Trim();
}
