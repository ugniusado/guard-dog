using Microsoft.EntityFrameworkCore;

namespace GuardDog.Worker;

// ── Sample entities ───────────────────────────────────────────────────────────
// These demonstrate the full Guard Dog feature set:
//   • EF Core model → SchemaSnapshot (SnapshotTool)
//   • SchemaSnapshot → Mermaid erDiagram (Phase 4)
//   • Drift detection against a live database (Phases 1 & 2)
//
// In a real project, replace this with your application's DbContext.

public sealed class Customer
{
    public int      Id        { get; set; }
    public string   Name      { get; set; } = string.Empty;
    public string?  Email     { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<Order> Orders { get; set; } = [];
}

public sealed class Order
{
    public int      Id         { get; set; }
    public int      CustomerId { get; set; }
    public decimal  Total      { get; set; }
    public string   Status     { get; set; } = "Pending";
    public DateTime CreatedAt  { get; set; }

    public Customer              Customer   { get; set; } = null!;
    public ICollection<OrderItem> OrderItems { get; set; } = [];
}

public sealed class OrderItem
{
    public int     Id        { get; set; }
    public int     OrderId   { get; set; }
    public int     ProductId { get; set; }
    public int     Quantity  { get; set; }
    public decimal UnitPrice { get; set; }

    public Order   Order   { get; set; } = null!;
    public Product Product { get; set; } = null!;
}

public sealed class Product
{
    public int     Id            { get; set; }
    public string  Name          { get; set; } = string.Empty;
    public string? Description   { get; set; }
    public decimal Price         { get; set; }
    public int     StockQuantity { get; set; }

    public ICollection<OrderItem> OrderItems { get; set; } = [];
}

// ── DbContext ─────────────────────────────────────────────────────────────────

/// <summary>
/// Sample DbContext used by GuardDog.SnapshotTool in CI/CD to demonstrate
/// schema snapshot generation and Mermaid ER diagram output.
///
/// In production: replace with your application's actual DbContext.
/// </summary>
public sealed class SampleDbContext : DbContext
{
    public SampleDbContext(DbContextOptions<SampleDbContext> options) : base(options) { }

    public DbSet<Customer>  Customers  { get; set; }
    public DbSet<Order>     Orders     { get; set; }
    public DbSet<OrderItem> OrderItems { get; set; }
    public DbSet<Product>   Products   { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(e =>
        {
            e.ToTable("Customers");
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.Email).HasMaxLength(256);
            e.Property(c => c.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            e.HasIndex(c => c.Email).IsUnique().HasDatabaseName("IX_Customers_Email");
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.ToTable("Orders");
            e.HasKey(o => o.Id);
            e.Property(o => o.Total).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(o => o.Status).HasMaxLength(50).IsRequired();
            e.Property(o => o.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            e.HasOne(o => o.Customer)
             .WithMany(c => c.Orders)
             .HasForeignKey(o => o.CustomerId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderItem>(e =>
        {
            e.ToTable("OrderItems");
            e.HasKey(oi => oi.Id);
            e.Property(oi => oi.UnitPrice).HasColumnType("decimal(18,2)").IsRequired();
            e.HasOne(oi => oi.Order)
             .WithMany(o => o.OrderItems)
             .HasForeignKey(oi => oi.OrderId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(oi => oi.Product)
             .WithMany(p => p.OrderItems)
             .HasForeignKey(oi => oi.ProductId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Product>(e =>
        {
            e.ToTable("Products");
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(300).IsRequired();
            e.Property(p => p.Description).HasMaxLength(2000);
            e.Property(p => p.Price).HasColumnType("decimal(18,2)").IsRequired();
        });
    }
}
