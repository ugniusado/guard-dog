namespace GuardDog.Core.Models;

/// <summary>
/// A single detected schema difference between the code model and the live database.
/// </summary>
public sealed record DriftItem
{
    public required string TableSchema  { get; init; }
    public required string TableName    { get; init; }
    public required string ObjectName   { get; init; }   // column, index, or FK name
    public required DriftKind Kind      { get; init; }
    public required DriftSeverity Severity { get; init; }

    /// <summary>What the code/snapshot says this object should look like.</summary>
    public required string CodeState    { get; init; }

    /// <summary>What was actually found in the live database.</summary>
    public required string DatabaseState { get; init; }

    /// <summary>
    /// A provider-specific SQL script that, when executed, will bring the database
    /// in line with the code model for this specific drift item. Null when no safe
    /// automated fix can be generated (e.g., destructive type changes).
    /// </summary>
    public string? FixScript { get; init; }

    public string FullTableName => TableSchema is { Length: > 0 }
        ? $"{TableSchema}.{TableName}"
        : TableName;

    public override string ToString() =>
        $"[{Severity}] {Kind} on {FullTableName}.{ObjectName}: " +
        $"code='{CodeState}' db='{DatabaseState}'";
}
