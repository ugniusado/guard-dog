using GuardDog.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace GuardDog.Core.Snapshot;

/// <summary>
/// Generates a <see cref="SchemaSnapshot"/> from the EF Core model embedded in
/// a <see cref="DbContext"/>.  This snapshot is the "Shadow Schema" that is
/// produced during CI/CD and embedded in the worker service binary so the worker
/// can compare the deployed code's expectations against the live database even
/// when running in a separate container.
/// </summary>
public interface ISnapshotGenerator
{
    SchemaSnapshot Generate(DbContext context, string version = "1.0.0");
}
