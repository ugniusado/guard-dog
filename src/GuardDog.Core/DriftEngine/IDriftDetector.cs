using GuardDog.Core.Models;
using GuardDog.Core.Schema;

namespace GuardDog.Core.DriftEngine;

/// <summary>
/// Compares a <see cref="SchemaSnapshot"/> (the expected code model) against a
/// <see cref="LiveSchema"/> (the actual database state) and produces a
/// <see cref="DriftReport"/> describing every difference found.
/// </summary>
public interface IDriftDetector
{
    DriftReport Detect(
        SchemaSnapshot snapshot,
        LiveSchema      liveSchema,
        string          provider,
        string          dataSource);
}
