namespace GuardDog.Core.Schema;

/// <summary>
/// Resolves the correct <see cref="IDatabaseSchemaReader"/> implementation
/// based on the configured provider name.
/// </summary>
public static class DatabaseSchemaReaderFactory
{
    public static IDatabaseSchemaReader Create(string provider) =>
        provider.ToLowerInvariant() switch
        {
            "sqlserver" or "mssql" => new SqlServerSchemaReader(),
            "postgresql" or "postgres" or "npgsql" => new PostgreSqlSchemaReader(),
            _ => throw new NotSupportedException(
                $"Provider '{provider}' is not supported. " +
                $"Supported values: SqlServer, PostgreSQL.")
        };
}
