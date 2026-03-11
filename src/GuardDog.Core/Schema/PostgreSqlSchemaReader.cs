using Npgsql;

namespace GuardDog.Core.Schema;

/// <summary>
/// Reads the live schema from a PostgreSQL database using INFORMATION_SCHEMA
/// and pg_catalog queries.  Requires USAGE on information_schema + pg_catalog.
/// </summary>
public sealed class PostgreSqlSchemaReader : IDatabaseSchemaReader
{
    public async Task<LiveSchema> ReadAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var columns     = await ReadColumnsAsync(conn, ct);
        var primaryKeys = await ReadPrimaryKeysAsync(conn, ct);
        var indexes     = await ReadIndexesAsync(conn, ct);
        var foreignKeys = await ReadForeignKeysAsync(conn, ct);

        var tableKeys = columns
            .Select(c => (c.Schema, c.Table))
            .Distinct()
            .OrderBy(t => t.Schema)
            .ThenBy(t => t.Table);

        var tables = tableKeys.Select(key =>
        {
            var tableCols = columns
                .Where(c => c.Schema == key.Schema && c.Table == key.Table)
                .Select(c => new LiveColumn(c.Name, c.StoreType, c.IsNullable, c.DefaultSql, c.MaxLength))
                .ToList();

            var pkName = primaryKeys
                .Where(pk => pk.Schema == key.Schema && pk.Table == key.Table)
                .Select(pk => pk.Name)
                .FirstOrDefault();
            var pkCols = primaryKeys
                .Where(pk => pk.Schema == key.Schema && pk.Table == key.Table)
                .OrderBy(pk => pk.OrdinalPosition)
                .Select(pk => pk.Column)
                .ToList();

            var tableIndexes = indexes
                .Where(i => i.Schema == key.Schema && i.Table == key.Table)
                .GroupBy(i => (i.IndexName, i.IsUnique))
                .Select(g => new LiveIndex(
                    g.Key.IndexName,
                    g.Select(r => r.Column).ToList(),
                    g.Key.IsUnique))
                .ToList();

            var tableFks = foreignKeys
                .Where(f => f.Schema == key.Schema && f.Table == key.Table)
                .GroupBy(f => (f.FkName, f.PrincipalSchema, f.PrincipalTable, f.OnDelete))
                .Select(g => new LiveForeignKey(
                    g.Key.FkName,
                    g.OrderBy(r => r.OrdinalPosition).Select(r => r.Column).ToList(),
                    g.Key.PrincipalSchema,
                    g.Key.PrincipalTable,
                    g.OrderBy(r => r.OrdinalPosition).Select(r => r.PrincipalColumn).ToList(),
                    g.Key.OnDelete))
                .ToList();

            return new LiveTable(key.Schema, key.Table, pkName, pkCols,
                tableCols, tableIndexes, tableFks);
        }).ToList();

        return new LiveSchema(tables);
    }

    // ── Columns ───────────────────────────────────────────────────────────────

    private static async Task<List<ColumnRow>> ReadColumnsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                c.table_schema,
                c.table_name,
                c.column_name,
                c.data_type,
                c.udt_name,
                c.is_nullable,
                c.character_maximum_length,
                c.column_default,
                (c.column_default LIKE 'nextval(%')::bool AS is_identity
            FROM information_schema.columns c
            JOIN information_schema.tables t
                ON t.table_schema = c.table_schema
               AND t.table_name   = c.table_name
               AND t.table_type   = 'BASE TABLE'
            WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var oSchema   = reader.GetOrdinal("table_schema");
        var oTable    = reader.GetOrdinal("table_name");
        var oCol      = reader.GetOrdinal("column_name");
        var oDataType = reader.GetOrdinal("data_type");
        var oUdtName  = reader.GetOrdinal("udt_name");
        var oNullable = reader.GetOrdinal("is_nullable");
        var oMaxLen   = reader.GetOrdinal("character_maximum_length");
        var oDefault  = reader.GetOrdinal("column_default");

        var rows = new List<ColumnRow>();
        while (await reader.ReadAsync(ct))
        {
            var udtName   = reader.GetString(oUdtName);
            var dataType  = reader.GetString(oDataType);
            var maxLength = reader.IsDBNull(oMaxLen) ? (int?)null : reader.GetInt32(oMaxLen);

            // Prefer udt_name for user-defined & composite types (e.g., "citext")
            var storeType = dataType == "USER-DEFINED" ? udtName : dataType;
            if (maxLength.HasValue && maxLength.Value != -1)
                storeType = $"{storeType}({maxLength})";

            rows.Add(new ColumnRow(
                Schema:    reader.GetString(oSchema),
                Table:     reader.GetString(oTable),
                Name:      reader.GetString(oCol),
                StoreType: storeType,
                IsNullable: reader.GetString(oNullable).Equals("YES", StringComparison.OrdinalIgnoreCase),
                DefaultSql: reader.IsDBNull(oDefault) ? null : reader.GetString(oDefault),
                MaxLength:  maxLength));
        }
        return rows;
    }

    // ── Primary Keys ─────────────────────────────────────────────────────────

    private static async Task<List<PkRow>> ReadPrimaryKeysAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                tc.table_schema,
                tc.table_name,
                tc.constraint_name,
                kcu.column_name,
                kcu.ordinal_position
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON tc.constraint_name = kcu.constraint_name
               AND tc.table_schema    = kcu.table_schema
            WHERE tc.constraint_type = 'PRIMARY KEY'
              AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY tc.table_schema, tc.table_name, kcu.ordinal_position
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var oSchema  = reader.GetOrdinal("table_schema");
        var oTable   = reader.GetOrdinal("table_name");
        var oName    = reader.GetOrdinal("constraint_name");
        var oCol     = reader.GetOrdinal("column_name");
        var oOrdinal = reader.GetOrdinal("ordinal_position");

        var rows = new List<PkRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new PkRow(
                reader.GetString(oSchema),
                reader.GetString(oTable),
                reader.GetString(oName),
                reader.GetString(oCol),
                reader.GetInt32(oOrdinal)));
        return rows;
    }

    // ── Indexes ──────────────────────────────────────────────────────────────

    private static async Task<List<IndexRow>> ReadIndexesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                n.nspname  AS schema_name,
                t.relname  AS table_name,
                i.relname  AS index_name,
                ix.indisunique AS is_unique,
                a.attname  AS column_name
            FROM pg_class t
            JOIN pg_index ix       ON t.oid          = ix.indrelid
            JOIN pg_class i        ON i.oid           = ix.indexrelid
            JOIN pg_namespace n    ON t.relnamespace  = n.oid
            JOIN pg_attribute a    ON a.attrelid       = t.oid
                                  AND a.attnum         = ANY(ix.indkey)
            WHERE t.relkind = 'r'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
              AND NOT ix.indisprimary
            ORDER BY n.nspname, t.relname, i.relname
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var oSchema = reader.GetOrdinal("schema_name");
        var oTable  = reader.GetOrdinal("table_name");
        var oIndex  = reader.GetOrdinal("index_name");
        var oUnique = reader.GetOrdinal("is_unique");
        var oCol    = reader.GetOrdinal("column_name");

        var rows = new List<IndexRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new IndexRow(
                reader.GetString(oSchema),
                reader.GetString(oTable),
                reader.GetString(oIndex),
                reader.GetBoolean(oUnique),
                reader.GetString(oCol)));
        return rows;
    }

    // ── Foreign Keys ─────────────────────────────────────────────────────────

    private static async Task<List<FkRow>> ReadForeignKeysAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                tc.table_schema,
                tc.table_name,
                tc.constraint_name,
                kcu.column_name,
                kcu.ordinal_position,
                ccu.table_schema AS principal_schema,
                ccu.table_name   AS principal_table,
                ccu.column_name  AS principal_column,
                rc.delete_rule
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON tc.constraint_name = kcu.constraint_name
               AND tc.table_schema    = kcu.table_schema
            JOIN information_schema.referential_constraints rc
                ON tc.constraint_name = rc.constraint_name
            JOIN information_schema.constraint_column_usage ccu
                ON rc.unique_constraint_name = ccu.constraint_name
               AND kcu.ordinal_position      = ccu.ordinal_position
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY tc.table_schema, tc.table_name, kcu.ordinal_position
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var oSchema   = reader.GetOrdinal("table_schema");
        var oTable    = reader.GetOrdinal("table_name");
        var oFkName   = reader.GetOrdinal("constraint_name");
        var oCol      = reader.GetOrdinal("column_name");
        var oOrdinal  = reader.GetOrdinal("ordinal_position");
        var oPSchema  = reader.GetOrdinal("principal_schema");
        var oPTable   = reader.GetOrdinal("principal_table");
        var oPCol     = reader.GetOrdinal("principal_column");
        var oOnDelete = reader.GetOrdinal("delete_rule");

        var rows = new List<FkRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new FkRow(
                reader.GetString(oSchema),
                reader.GetString(oTable),
                reader.GetString(oFkName),
                reader.GetString(oCol),
                reader.GetInt32(oOrdinal),
                reader.GetString(oPSchema),
                reader.GetString(oPTable),
                reader.GetString(oPCol),
                reader.GetString(oOnDelete)));
        return rows;
    }

    // ── Private row types ────────────────────────────────────────────────────
    private sealed record ColumnRow(string Schema, string Table, string Name, string StoreType,
        bool IsNullable, string? DefaultSql, int? MaxLength);
    private sealed record PkRow(string Schema, string Table, string Name, string Column, int OrdinalPosition);
    private sealed record IndexRow(string Schema, string Table, string IndexName, bool IsUnique, string Column);
    private sealed record FkRow(string Schema, string Table, string FkName, string Column,
        int OrdinalPosition, string PrincipalSchema, string PrincipalTable,
        string PrincipalColumn, string OnDelete);
}
