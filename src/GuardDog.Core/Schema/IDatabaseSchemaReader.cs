namespace GuardDog.Core.Schema;

/// <summary>
/// Reads the actual schema from a live database using INFORMATION_SCHEMA and
/// provider-specific system catalogue queries.
/// </summary>
public interface IDatabaseSchemaReader
{
    /// <summary>
    /// Returns the live schema for all user tables visible via the supplied
    /// connection string.  Callers should use a read-only login that has only
    /// VIEW DEFINITION / SELECT on system catalogues — never db_owner.
    /// </summary>
    Task<LiveSchema> ReadAsync(string connectionString, CancellationToken ct = default);
}
