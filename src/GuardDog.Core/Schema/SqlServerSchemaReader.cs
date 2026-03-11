using System.Data;
using Microsoft.Data.SqlClient;

namespace GuardDog.Core.Schema;

/// <summary>
/// Reads the live schema from a SQL Server / Azure SQL database.
/// Requires at minimum: SELECT on INFORMATION_SCHEMA views + VIEW DEFINITION.
/// </summary>
public sealed class SqlServerSchemaReader : IDatabaseSchemaReader
{
    public async Task<LiveSchema> ReadAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var columns     = await ReadColumnsAsync(conn, ct);
        var primaryKeys = await ReadPrimaryKeysAsync(conn, ct);
        var indexes     = await ReadIndexesAsync(conn, ct);
        var foreignKeys = await ReadForeignKeysAsync(conn, ct);

        // Group into tables
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

            var pkEntry  = primaryKeys.FirstOrDefault(pk => pk.Schema == key.Schema && pk.Table == key.Table);
            var pkName   = pkEntry?.Name;
            var pkCols   = primaryKeys
                .Where(pk => pk.Schema == key.Schema && pk.Table == key.Table)
                .OrderBy(pk => pk.OrdinalPosition)
                .Select(pk => pk.Column)
                .ToList();

            var tableIndexes = indexes
                .Where(i => i.Schema == key.Schema && i.Table == key.Table)
                .GroupBy(i => (i.IndexName, i.IsUnique))
                .Select(g => new LiveIndex(
                    g.Key.IndexName,
                    g.OrderBy(r => r.KeyOrdinal).Select(r => r.Column).ToList(),
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

    private static async Task<List<ColumnRow>> ReadColumnsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                c.TABLE_SCHEMA,
                c.TABLE_NAME,
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                c.CHARACTER_MAXIMUM_LENGTH,
                c.COLUMN_DEFAULT,
                COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA)+'.'+QUOTENAME(c.TABLE_NAME)),
                               c.COLUMN_NAME, 'IsIdentity') AS IS_IDENTITY
            FROM INFORMATION_SCHEMA.COLUMNS c
            INNER JOIN INFORMATION_SCHEMA.TABLES t
                ON t.TABLE_SCHEMA = c.TABLE_SCHEMA
               AND t.TABLE_NAME   = c.TABLE_NAME
               AND t.TABLE_TYPE   = 'BASE TABLE'
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<ColumnRow>();
        while (await reader.ReadAsync(ct))
        {
            var dataType  = reader.GetString("DATA_TYPE");
            var maxLength = reader.IsDBNull("CHARACTER_MAXIMUM_LENGTH")
                ? (int?)null
                : reader.GetInt32("CHARACTER_MAXIMUM_LENGTH");

            var storeType = maxLength.HasValue && maxLength.Value != -1
                ? $"{dataType}({maxLength})"
                : dataType;

            rows.Add(new ColumnRow(
                Schema:    reader.GetString("TABLE_SCHEMA"),
                Table:     reader.GetString("TABLE_NAME"),
                Name:      reader.GetString("COLUMN_NAME"),
                StoreType: storeType,
                IsNullable: reader.GetString("IS_NULLABLE").Equals("YES", StringComparison.OrdinalIgnoreCase),
                DefaultSql: reader.IsDBNull("COLUMN_DEFAULT") ? null : reader.GetString("COLUMN_DEFAULT"),
                MaxLength:  maxLength));
        }
        return rows;
    }

    // ── Primary Keys ─────────────────────────────────────────────────────────

    private static async Task<List<PkRow>> ReadPrimaryKeysAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                tc.TABLE_SCHEMA,
                tc.TABLE_NAME,
                tc.CONSTRAINT_NAME,
                kcu.COLUMN_NAME,
                kcu.ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
               AND tc.TABLE_SCHEMA    = kcu.TABLE_SCHEMA
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ORDER BY tc.TABLE_SCHEMA, tc.TABLE_NAME, kcu.ORDINAL_POSITION
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<PkRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new PkRow(
                reader.GetString("TABLE_SCHEMA"),
                reader.GetString("TABLE_NAME"),
                reader.GetString("CONSTRAINT_NAME"),
                reader.GetString("COLUMN_NAME"),
                reader.GetInt32("ORDINAL_POSITION")));
        return rows;
    }

    // ── Indexes ──────────────────────────────────────────────────────────────

    private static async Task<List<IndexRow>> ReadIndexesAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                s.name  AS schema_name,
                t.name  AS table_name,
                i.name  AS index_name,
                i.is_unique,
                c.name  AS column_name,
                ic.key_ordinal
            FROM sys.indexes i
            JOIN sys.tables       t  ON i.object_id    = t.object_id
            JOIN sys.schemas      s  ON t.schema_id    = s.schema_id
            JOIN sys.index_columns ic ON i.object_id   = ic.object_id
                                     AND i.index_id    = ic.index_id
            JOIN sys.columns      c  ON ic.object_id   = c.object_id
                                     AND ic.column_id  = c.column_id
            WHERE i.is_primary_key = 0
              AND i.type           > 0
              AND ic.is_included_column = 0
            ORDER BY s.name, t.name, i.name, ic.key_ordinal
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<IndexRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new IndexRow(
                reader.GetString("schema_name"),
                reader.GetString("table_name"),
                reader.GetString("index_name"),
                reader.GetBoolean("is_unique"),
                reader.GetString("column_name"),
                reader.GetByte("key_ordinal")));
        return rows;
    }

    // ── Foreign Keys ─────────────────────────────────────────────────────────

    private static async Task<List<FkRow>> ReadForeignKeysAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                tc.TABLE_SCHEMA,
                tc.TABLE_NAME,
                tc.CONSTRAINT_NAME,
                kcu.COLUMN_NAME,
                kcu.ORDINAL_POSITION,
                ccu.TABLE_SCHEMA AS PRINCIPAL_SCHEMA,
                ccu.TABLE_NAME   AS PRINCIPAL_TABLE,
                ccu.COLUMN_NAME  AS PRINCIPAL_COLUMN,
                rc.DELETE_RULE
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
               AND tc.TABLE_SCHEMA    = kcu.TABLE_SCHEMA
            JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
                ON tc.CONSTRAINT_NAME = rc.CONSTRAINT_NAME
            JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE ccu
                ON rc.UNIQUE_CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
               AND kcu.ORDINAL_POSITION      = ccu.ORDINAL_POSITION
            WHERE tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
            ORDER BY tc.TABLE_SCHEMA, tc.TABLE_NAME, kcu.ORDINAL_POSITION
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<FkRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new FkRow(
                reader.GetString("TABLE_SCHEMA"),
                reader.GetString("TABLE_NAME"),
                reader.GetString("CONSTRAINT_NAME"),
                reader.GetString("COLUMN_NAME"),
                reader.GetInt32("ORDINAL_POSITION"),
                reader.GetString("PRINCIPAL_SCHEMA"),
                reader.GetString("PRINCIPAL_TABLE"),
                reader.GetString("PRINCIPAL_COLUMN"),
                reader.GetString("DELETE_RULE")));
        return rows;
    }

    // ── Private row types ────────────────────────────────────────────────────
    private sealed record ColumnRow(string Schema, string Table, string Name, string StoreType,
        bool IsNullable, string? DefaultSql, int? MaxLength);
    private sealed record PkRow(string Schema, string Table, string Name, string Column, int OrdinalPosition);
    private sealed record IndexRow(string Schema, string Table, string IndexName, bool IsUnique,
        string Column, int KeyOrdinal);
    private sealed record FkRow(string Schema, string Table, string FkName, string Column,
        int OrdinalPosition, string PrincipalSchema, string PrincipalTable,
        string PrincipalColumn, string OnDelete);
}
