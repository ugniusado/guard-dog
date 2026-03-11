namespace GuardDog.Core.Models;

/// <summary>
/// Classifies the impact of a detected schema drift item.
/// </summary>
public enum DriftSeverity
{
    /// <summary>
    /// The application will fail at runtime — e.g., a missing column that EF will
    /// attempt to read or write on every request.
    /// </summary>
    Critical,

    /// <summary>
    /// The application may encounter errors under specific conditions — e.g., a
    /// column that should be NOT NULL is nullable in the DB, causing constraint
    /// violations on insert.
    /// </summary>
    Warning,

    /// <summary>
    /// The drift will not break the application — e.g., an extra index added by a
    /// DBA for query performance, or an extra column the ORM will simply ignore.
    /// </summary>
    Informational
}
