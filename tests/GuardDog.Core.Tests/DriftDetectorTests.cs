using FluentAssertions;
using GuardDog.Core.DriftEngine;
using GuardDog.Core.Models;
using GuardDog.Core.Schema;

namespace GuardDog.Core.Tests;

public class DriftDetectorTests
{
    private readonly DriftDetector _sut = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SchemaSnapshot BuildSnapshot(params TableSnapshot[] tables) => new()
    {
        Version     = "1.0.0",
        GeneratedAt = DateTimeOffset.UtcNow,
        Provider    = "SqlServer",
        ModelHash   = "test",
        Tables      = tables
    };

    private static TableSnapshot BuildTable(string name,
        string schema = "dbo",
        ColumnSnapshot[]? columns = null,
        string[]? pkCols = null,
        IndexSnapshot[]? indexes = null,
        ForeignKeySnapshot[]? fks = null) => new()
    {
        Schema            = schema,
        Name              = name,
        Columns           = columns ?? DefaultColumns(),
        PrimaryKeyName    = "PK_" + name,
        PrimaryKeyColumns = pkCols ?? ["Id"],
        Indexes           = indexes ?? [],
        ForeignKeys       = fks ?? []
    };

    private static ColumnSnapshot[] DefaultColumns() =>
    [
        new() { Name = "Id",   StoreType = "int",          IsNullable = false, IsIdentity = true },
        new() { Name = "Name", StoreType = "nvarchar(200)", IsNullable = false, IsIdentity = false }
    ];

    private static LiveSchema BuildLiveSchema(params LiveTable[] tables) =>
        new(tables);

    private static LiveTable BuildLiveTable(string name,
        string schema = "dbo",
        LiveColumn[]? columns = null,
        string[]? pkCols = null,
        LiveIndex[]? indexes = null,
        LiveForeignKey[]? fks = null) => new(
            Schema: schema,
            Name: name,
            PrimaryKeyName: "PK_" + name,
            PrimaryKeyColumns: pkCols ?? ["Id"],
            Columns: columns ?? DefaultLiveColumns(),
            Indexes: indexes ?? [],
            ForeignKeys: fks ?? []);

    private static LiveColumn[] DefaultLiveColumns() =>
    [
        new("Id",   "int",           false, null, null),
        new("Name", "nvarchar(200)", false, null, null)
    ];

    // ── No Drift ──────────────────────────────────────────────────────────────

    [Fact]
    public void Detect_WhenSchemasMatch_ReturnsNoDrift()
    {
        var snapshot   = BuildSnapshot(BuildTable("Customers"));
        var liveSchema = BuildLiveSchema(BuildLiveTable("Customers"));

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        report.HasDrift.Should().BeFalse();
        report.Items.Should().BeEmpty();
    }

    // ── Missing Table ─────────────────────────────────────────────────────────

    [Fact]
    public void Detect_WhenTableMissingFromDb_ReturnsCriticalDrift()
    {
        var snapshot   = BuildSnapshot(BuildTable("Orders"));
        var liveSchema = BuildLiveSchema(); // empty DB

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        report.HasCriticalDrift.Should().BeTrue();
        report.Items.Should().ContainSingle(i =>
            i.Kind     == DriftKind.MissingTable &&
            i.Severity == DriftSeverity.Critical &&
            i.TableName == "Orders");
    }

    [Fact]
    public void Detect_WhenTableMissingFromDb_FixScriptContainsCreateTable()
    {
        var snapshot   = BuildSnapshot(BuildTable("Orders"));
        var liveSchema = BuildLiveSchema();

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        report.Items.First().FixScript.Should().Contain("CREATE TABLE");
        report.Items.First().FixScript.Should().Contain("Orders");
    }

    // ── Extra Table ───────────────────────────────────────────────────────────

    [Fact]
    public void Detect_WhenExtraTableInDb_ReturnsInformationalDrift()
    {
        var snapshot   = BuildSnapshot(); // no tables in code model
        var liveSchema = BuildLiveSchema(BuildLiveTable("LegacyData"));

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        report.HasDrift.Should().BeTrue();
        report.HasCriticalDrift.Should().BeFalse();
        report.Items.Should().ContainSingle(i =>
            i.Kind     == DriftKind.ExtraTable &&
            i.Severity == DriftSeverity.Informational);
    }

    // ── Missing Column ────────────────────────────────────────────────────────

    [Fact]
    public void Detect_WhenColumnMissingFromDb_ReturnsCriticalDrift()
    {
        var snapshot = BuildSnapshot(BuildTable("Customers", columns:
        [
            new() { Name = "Id",    StoreType = "int",          IsNullable = false, IsIdentity = true },
            new() { Name = "Name",  StoreType = "nvarchar(200)", IsNullable = false, IsIdentity = false },
            new() { Name = "Email", StoreType = "nvarchar(256)", IsNullable = false, IsIdentity = false }
        ]));

        var liveSchema = BuildLiveSchema(BuildLiveTable("Customers",
            columns: DefaultLiveColumns())); // Email is missing

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        report.Items.Should().ContainSingle(i =>
            i.Kind       == DriftKind.MissingColumn &&
            i.Severity   == DriftSeverity.Critical &&
            i.ObjectName == "Email");
    }

    [Fact]
    public void Detect_WhenColumnMissingFromDb_FixScriptContainsAlterTable()
    {
        var snapshot = BuildSnapshot(BuildTable("Customers", columns:
        [
            new() { Name = "Id",    StoreType = "int",          IsNullable = false, IsIdentity = true },
            new() { Name = "Email", StoreType = "nvarchar(256)", IsNullable = false, IsIdentity = false }
        ]));
        var liveSchema = BuildLiveSchema(BuildLiveTable("Customers",
            columns: [new("Id", "int", false, null, null)]));

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        var item = report.Items.First(i => i.Kind == DriftKind.MissingColumn);
        item.FixScript.Should().Contain("ALTER TABLE");
        item.FixScript.Should().Contain("Email");
    }

    // ── Type Mismatch ─────────────────────────────────────────────────────────

    [Fact]
    public void Detect_WhenColumnTypeMismatch_ReturnsCriticalDrift()
    {
        var snapshot = BuildSnapshot(BuildTable("Products", columns:
        [
            new() { Name = "Price", StoreType = "decimal(18,2)", IsNullable = false, IsIdentity = false }
        ]));
        var liveSchema = BuildLiveSchema(BuildLiveTable("Products",
            columns: [new("Price", "float", false, null, null)],
            pkCols: []));

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        report.Items.Should().Contain(i =>
            i.Kind     == DriftKind.ColumnTypeMismatch &&
            i.Severity == DriftSeverity.Critical &&
            i.ObjectName == "Price");
    }

    // ── Nullability Mismatch ──────────────────────────────────────────────────

    [Fact]
    public void Detect_WhenNullabilityMismatch_ReturnsWarning()
    {
        var snapshot = BuildSnapshot(BuildTable("Users", columns:
        [
            new() { Name = "Id",   StoreType = "int",          IsNullable = false, IsIdentity = true },
            new() { Name = "Bio",  StoreType = "nvarchar(max)", IsNullable = false, IsIdentity = false }
        ]));
        var liveSchema = BuildLiveSchema(BuildLiveTable("Users",
            columns:
            [
                new("Id",  "int",          false, null, null),
                new("Bio", "nvarchar(max)", true, null, null)   // nullable in DB, NOT NULL in code
            ]));

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        report.Items.Should().Contain(i =>
            i.Kind     == DriftKind.NullabilityMismatch &&
            i.Severity == DriftSeverity.Warning &&
            i.ObjectName == "Bio");
    }

    // ── Missing Index ─────────────────────────────────────────────────────────

    [Fact]
    public void Detect_WhenIndexMissingFromDb_ReturnsInformational()
    {
        var idx = new IndexSnapshot
        {
            Name    = "IX_Customers_Email",
            Columns = ["Email"],
            IsUnique = true
        };

        var snapshot = BuildSnapshot(BuildTable("Customers",
            columns:
            [
                new() { Name = "Id",    StoreType = "int",          IsNullable = false, IsIdentity = true },
                new() { Name = "Email", StoreType = "nvarchar(256)", IsNullable = false, IsIdentity = false }
            ],
            indexes: [idx]));

        var liveSchema = BuildLiveSchema(BuildLiveTable("Customers",
            columns:
            [
                new("Id",    "int",          false, null, null),
                new("Email", "nvarchar(256)", false, null, null)
            ]));

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        report.Items.Should().Contain(i =>
            i.Kind     == DriftKind.MissingIndex &&
            i.Severity == DriftSeverity.Informational &&
            i.ObjectName == "IX_Customers_Email");
    }

    // ── Aggregated Fix Script ─────────────────────────────────────────────────

    [Fact]
    public void DriftReport_AggregatedFixScript_IncludesAllFixableItems()
    {
        var snapshot   = BuildSnapshot(BuildTable("Orders"));
        var liveSchema = BuildLiveSchema(); // Orders is missing

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        report.AggregatedFixScript.Should().NotBeNull();
        report.AggregatedFixScript.Should().Contain("CREATE TABLE");
    }

    // ── Case Insensitive Matching ─────────────────────────────────────────────

    [Fact]
    public void Detect_TableAndColumnNamesAreCaseInsensitive()
    {
        var snapshot = BuildSnapshot(BuildTable("customers")); // lowercase in code
        var liveSchema = BuildLiveSchema(BuildLiveTable("Customers")); // PascalCase in DB

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        // Should match despite case difference
        report.HasCriticalDrift.Should().BeFalse("table names should match case-insensitively");
    }

    // ── Multi-table scenario ──────────────────────────────────────────────────

    [Fact]
    public void Detect_MultipleDriftItems_AllCaptured()
    {
        var snapshot = BuildSnapshot(
            BuildTable("Customers"),
            BuildTable("Orders"),   // will be missing
            BuildTable("Products")  // will have column mismatch
        );

        var liveSchema = BuildLiveSchema(
            BuildLiveTable("Customers"),
            // Orders is absent
            BuildLiveTable("Products", columns:
            [
                new("Id",    "int",   false, null, null),
                new("Name",  "text",  false, null, null)  // type mismatch: code expects nvarchar(200)
            ])
        );

        var report = _sut.Detect(snapshot, liveSchema, "SqlServer", "test-db");

        report.Items.Should().Contain(i => i.Kind == DriftKind.MissingTable && i.TableName == "Orders");
        report.Items.Should().Contain(i => i.Kind == DriftKind.ColumnTypeMismatch && i.TableName == "Products");
    }
}
