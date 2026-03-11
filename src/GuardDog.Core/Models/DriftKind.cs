namespace GuardDog.Core.Models;

/// <summary>
/// Describes the nature of the schema difference detected.
/// </summary>
public enum DriftKind
{
    // ── Table-level ──────────────────────────────────────────────────────────
    MissingTable,           // Table exists in code model but NOT in the DB
    ExtraTable,             // Table exists in DB but NOT in code model (informational)

    // ── Column-level ─────────────────────────────────────────────────────────
    MissingColumn,          // Column expected by code is absent in DB
    ExtraColumn,            // Column in DB not known to the code model (informational)
    ColumnTypeMismatch,     // Column exists but store type differs
    NullabilityMismatch,    // Column exists but IS_NULLABLE differs
    DefaultValueMismatch,   // Column default value expression differs

    // ── Constraint / Index ───────────────────────────────────────────────────
    PrimaryKeyMismatch,     // PK columns or name differ
    MissingIndex,           // Index defined in code is absent in DB
    ExtraIndex,             // Index in DB not defined in code (informational)
    MissingForeignKey,      // FK constraint expected by code is absent
    ExtraForeignKey         // FK in DB not expected by code (informational)
}
