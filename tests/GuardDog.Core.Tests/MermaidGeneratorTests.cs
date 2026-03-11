using FluentAssertions;
using GuardDog.Core.Diagram;
using GuardDog.Core.Models;

namespace GuardDog.Core.Tests;

public class MermaidGeneratorTests
{
    private readonly MermaidErDiagramGenerator _sut = new();

    private static SchemaSnapshot BuildSnapshot(params TableSnapshot[] tables) => new()
    {
        Version     = "2.0.0",
        GeneratedAt = DateTimeOffset.UtcNow,
        Provider    = "SqlServer",
        ModelHash   = "test-hash",
        Tables      = tables
    };

    [Fact]
    public void Generate_EmptySnapshot_ReturnsValidMermaidBlock()
    {
        var snapshot = BuildSnapshot();
        var diagram  = _sut.Generate(snapshot);

        diagram.Should().Contain("```mermaid");
        diagram.Should().Contain("erDiagram");
        diagram.Should().Contain("```");
    }

    [Fact]
    public void Generate_SingleTable_IncludesTableAndColumns()
    {
        var snapshot = BuildSnapshot(new TableSnapshot
        {
            Schema = "dbo",
            Name   = "Customers",
            Columns =
            [
                new() { Name = "Id",   StoreType = "int",          IsNullable = false, IsIdentity = true },
                new() { Name = "Name", StoreType = "nvarchar(200)", IsNullable = false, IsIdentity = false }
            ],
            PrimaryKeyName    = "PK_Customers",
            PrimaryKeyColumns = ["Id"],
            Indexes           = [],
            ForeignKeys       = []
        });

        var diagram = _sut.Generate(snapshot);

        diagram.Should().Contain("Customers");
        diagram.Should().Contain("int Id");
        diagram.Should().Contain("nvarchar Name");  // length is stripped
        diagram.Should().Contain("PK");
    }

    [Fact]
    public void Generate_ForeignKey_RendersRelationship()
    {
        var customers = new TableSnapshot
        {
            Schema = "dbo", Name = "Customers",
            Columns =
            [
                new() { Name = "Id", StoreType = "int", IsNullable = false, IsIdentity = true }
            ],
            PrimaryKeyName = "PK_Customers", PrimaryKeyColumns = ["Id"],
            Indexes = [], ForeignKeys = []
        };

        var orders = new TableSnapshot
        {
            Schema = "dbo", Name = "Orders",
            Columns =
            [
                new() { Name = "Id",         StoreType = "int", IsNullable = false, IsIdentity = true },
                new() { Name = "CustomerId", StoreType = "int", IsNullable = false, IsIdentity = false }
            ],
            PrimaryKeyName    = "PK_Orders",
            PrimaryKeyColumns = ["Id"],
            Indexes           = [],
            ForeignKeys       =
            [
                new()
                {
                    Name             = "FK_Orders_Customers",
                    Columns          = ["CustomerId"],
                    PrincipalSchema  = "dbo",
                    PrincipalTable   = "Customers",
                    PrincipalColumns = ["Id"],
                    OnDelete         = "NO ACTION"
                }
            ]
        };

        var diagram = _sut.Generate(BuildSnapshot(customers, orders));

        // Should contain a relationship line between Customers and Orders
        diagram.Should().Contain("Customers");
        diagram.Should().Contain("Orders");
        diagram.Should().MatchRegex(@"Customers.*--.*Orders");
    }

    [Fact]
    public void Generate_IncludesFKAnnotationOnFkColumn()
    {
        var orders = new TableSnapshot
        {
            Schema = "dbo", Name = "Orders",
            Columns =
            [
                new() { Name = "Id",         StoreType = "int", IsNullable = false, IsIdentity = true },
                new() { Name = "CustomerId", StoreType = "int", IsNullable = false, IsIdentity = false }
            ],
            PrimaryKeyName    = "PK_Orders",
            PrimaryKeyColumns = ["Id"],
            Indexes           = [],
            ForeignKeys       =
            [
                new()
                {
                    Name = "FK_Orders_Customers", Columns = ["CustomerId"],
                    PrincipalSchema = "dbo", PrincipalTable = "Customers",
                    PrincipalColumns = ["Id"], OnDelete = "CASCADE"
                }
            ]
        };

        var diagram = _sut.Generate(BuildSnapshot(orders));

        diagram.Should().Contain("FK");
    }

    [Fact]
    public void Generate_IncludesSnapshotVersionInHeader()
    {
        var snapshot = BuildSnapshot();
        var diagram  = _sut.Generate(snapshot);

        diagram.Should().Contain("v2.0.0");
    }

    [Fact]
    public void Generate_TypesWithParentheses_AreStripped()
    {
        var table = new TableSnapshot
        {
            Schema = "dbo", Name = "Products",
            Columns =
            [
                new() { Name = "Price", StoreType = "decimal(18,2)", IsNullable = false, IsIdentity = false }
            ],
            PrimaryKeyName = null, PrimaryKeyColumns = [],
            Indexes = [], ForeignKeys = []
        };

        var diagram = _sut.Generate(BuildSnapshot(table));

        // The Mermaid output should have 'decimal' not 'decimal(18,2)'
        diagram.Should().Contain("decimal Price");
        diagram.Should().NotContain("decimal(18,2)");
    }

    [Fact]
    public void Generate_NullableColumn_HasNullableAnnotation()
    {
        var table = new TableSnapshot
        {
            Schema = "dbo", Name = "Users",
            Columns =
            [
                new() { Name = "Bio", StoreType = "nvarchar(max)", IsNullable = true, IsIdentity = false }
            ],
            PrimaryKeyName = null, PrimaryKeyColumns = [],
            Indexes = [], ForeignKeys = []
        };

        var diagram = _sut.Generate(BuildSnapshot(table));

        diagram.Should().Contain("nullable");
    }
}
