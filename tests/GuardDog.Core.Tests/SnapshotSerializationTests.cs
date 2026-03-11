using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using GuardDog.Core.Models;

namespace GuardDog.Core.Tests;

/// <summary>
/// Verifies that <see cref="SchemaSnapshot"/> round-trips correctly through
/// JSON serialisation so the CI/CD artifact → worker embedding pipeline works.
/// </summary>
public class SnapshotSerializationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented            = true,
        PropertyNamingPolicy     = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition   = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private static SchemaSnapshot BuildRichSnapshot() => new()
    {
        Version     = "3.1.4",
        GeneratedAt = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero),
        Provider    = "SqlServer",
        ModelHash   = "abc123",
        Tables      =
        [
            new()
            {
                Schema = "dbo",
                Name   = "Customers",
                PrimaryKeyName    = "PK_Customers",
                PrimaryKeyColumns = ["Id"],
                Columns =
                [
                    new() { Name = "Id",    StoreType = "int",          IsNullable = false, IsIdentity = true,  MaxLength = null },
                    new() { Name = "Name",  StoreType = "nvarchar(200)", IsNullable = false, IsIdentity = false, MaxLength = 200  },
                    new() { Name = "Email", StoreType = "nvarchar(256)", IsNullable = true,  IsIdentity = false, MaxLength = 256,
                            DefaultValueSql = "N''" }
                ],
                Indexes =
                [
                    new() { Name = "IX_Customers_Email", Columns = ["Email"], IsUnique = true }
                ],
                ForeignKeys = []
            },
            new()
            {
                Schema = "dbo",
                Name   = "Orders",
                PrimaryKeyName    = "PK_Orders",
                PrimaryKeyColumns = ["Id"],
                Columns =
                [
                    new() { Name = "Id",         StoreType = "int",      IsNullable = false, IsIdentity = true  },
                    new() { Name = "CustomerId", StoreType = "int",       IsNullable = false, IsIdentity = false },
                    new() { Name = "Total",      StoreType = "decimal(18,2)", IsNullable = false, IsIdentity = false }
                ],
                Indexes = [],
                ForeignKeys =
                [
                    new()
                    {
                        Name             = "FK_Orders_Customers_CustomerId",
                        Columns          = ["CustomerId"],
                        PrincipalSchema  = "dbo",
                        PrincipalTable   = "Customers",
                        PrincipalColumns = ["Id"],
                        OnDelete         = "CASCADE"
                    }
                ]
            }
        ]
    };

    [Fact]
    public void Snapshot_RoundTripsViaJson_WithoutDataLoss()
    {
        var original = BuildRichSnapshot();

        var json       = JsonSerializer.Serialize(original, JsonOpts);
        var roundTrip  = JsonSerializer.Deserialize<SchemaSnapshot>(json, JsonOpts);

        roundTrip.Should().NotBeNull();
        roundTrip!.Version.Should().Be(original.Version);
        roundTrip.Provider.Should().Be(original.Provider);
        roundTrip.ModelHash.Should().Be(original.ModelHash);
        roundTrip.Tables.Should().HaveCount(2);

        var customers = roundTrip.Tables.First(t => t.Name == "Customers");
        customers.Columns.Should().HaveCount(3);
        customers.Indexes.Should().HaveCount(1);
        customers.Indexes[0].IsUnique.Should().BeTrue();

        var orders = roundTrip.Tables.First(t => t.Name == "Orders");
        orders.ForeignKeys.Should().HaveCount(1);
        orders.ForeignKeys[0].OnDelete.Should().Be("CASCADE");
    }

    [Fact]
    public void Snapshot_JsonIsHumanReadable()
    {
        var snapshot = BuildRichSnapshot();
        var json     = JsonSerializer.Serialize(snapshot, JsonOpts);

        // camelCase property names
        json.Should().Contain("\"tables\"");
        json.Should().Contain("\"columns\"");
        json.Should().Contain("\"storeType\"");
        json.Should().Contain("\"isNullable\"");
        json.Should().Contain("\"foreignKeys\"");

        // Null fields should be omitted (DefaultIgnoreCondition = WhenWritingNull)
        // The Id column has null MaxLength — it should not appear
        var idLine = json.Split('\n')
            .FirstOrDefault(l => l.Contains("\"name\": \"Id\""));
        // The full Id object should not contain "maxLength" (it's null)
        // This is a weak check but confirms null-omission is working
        json.Should().NotContain("\"maxLength\": null");
    }

    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        var tables = BuildRichSnapshot().Tables;
        var hash1  = SchemaSnapshot.ComputeHash(tables);
        var hash2  = SchemaSnapshot.ComputeHash(tables);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeHash_ChangesWhenSchemaChanges()
    {
        var original = BuildRichSnapshot().Tables;

        // Modify one column type
        var modified = original
            .Select(t => t with
            {
                Columns = t.Columns
                    .Select(c => c.Name == "Email"
                        ? c with { StoreType = "nvarchar(512)" }
                        : c)
                    .ToList()
            })
            .ToList();

        var hashOriginal = SchemaSnapshot.ComputeHash(original);
        var hashModified = SchemaSnapshot.ComputeHash(modified);

        hashOriginal.Should().NotBe(hashModified);
    }

    [Fact]
    public void SchemaSnapshot_Empty_IsValid()
    {
        var empty = SchemaSnapshot.Empty;

        empty.Tables.Should().BeEmpty();
        empty.Version.Should().Be("0.0.0");
    }

    [Fact]
    public void Snapshot_CanDeserializeFromCamelCaseJson()
    {
        // Simulate JSON produced by GuardDog.SnapshotTool in CI
        const string json = """
            {
              "version": "1.0.0",
              "generatedAt": "2025-01-01T00:00:00+00:00",
              "provider": "SqlServer",
              "modelHash": "deadbeef",
              "tables": [
                {
                  "schema": "dbo",
                  "name": "Foo",
                  "primaryKeyName": "PK_Foo",
                  "primaryKeyColumns": ["Id"],
                  "columns": [
                    { "name": "Id", "storeType": "int", "isNullable": false, "isIdentity": true }
                  ],
                  "indexes": [],
                  "foreignKeys": []
                }
              ]
            }
            """;

        var snapshot = JsonSerializer.Deserialize<SchemaSnapshot>(json, JsonOpts);

        snapshot.Should().NotBeNull();
        snapshot!.Tables.Should().HaveCount(1);
        snapshot.Tables[0].Name.Should().Be("Foo");
    }
}
