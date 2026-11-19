using Ehr.Data.Accounts;
using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Orders;

// Maps ehr.orders and ehr.order_catalog_items (UC 1, Tahseen Arnan). EhrDbContext picks these up with
// ApplyConfigurationsFromAssembly. An order's type and item are one foreign key, so an order can only name an item of
// its own type.
public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> order)
    {
        order.ToTable("orders", table =>
        {
            table.HasCheckConstraint("ck_orders_type", $"type IN ({Codes.List(OrderType.All)})");
            table.HasCheckConstraint("ck_orders_status", $"status IN ({Codes.List(OrderStatus.All)})");
            table.HasCheckConstraint("ck_orders_details", "jsonb_typeof(details) = 'object'");
            // A discontinued order says why, who and when; an active one has none of the three.
            table.HasCheckConstraint("ck_orders_discontinued",
                $"(status = '{OrderStatus.Revoked}') = (discontinued_at IS NOT NULL) AND (discontinued_at IS NULL) = (discontinuer_id IS NULL) "
                + "AND (discontinued_at IS NULL) = (discontinue_reason IS NULL)");
            table.HasCheckConstraint("ck_orders_replaced", $"replaced_by_order_id IS NULL OR status = '{OrderStatus.Revoked}'");
            table.HasCheckConstraint("ck_orders_replaces_another", "replaces_order_id <> id AND replaced_by_order_id <> id");
        });

        order.Property(o => o.Type).HasMaxLength(20);
        order.Property(o => o.CatalogCode).HasMaxLength(20);
        order.Property(o => o.Details).HasColumnType("jsonb");
        order.Property(o => o.Status).HasMaxLength(20);
        order.Property(o => o.DuplicateReason).HasMaxLength(500);
        order.Property(o => o.SafetyWarnings).HasColumnType("jsonb");
        order.Property(o => o.SafetyOverrideReason).HasMaxLength(500);
        order.Property(o => o.DiscontinueReason).HasMaxLength(500);
        // The database's, as for signed notes: the signed-in user, and the time (db/policies/60-orders.sql sets it again).
        order.Property(o => o.SignerId).HasDefaultValueSql("NULLIF(current_setting('app.user_id', true), '')::uuid");
        order.Property(o => o.SignedAt).HasDefaultValueSql("now()");
        order.HasOne<Patient>().WithMany().HasForeignKey(o => o.PatientId).OnDelete(DeleteBehavior.Restrict);
        order.HasOne<StaffUser>().WithMany().HasForeignKey(o => o.SignerId).OnDelete(DeleteBehavior.Restrict);
        order.HasOne<OrderCatalogItem>().WithMany()
            .HasForeignKey(o => new { o.Type, o.CatalogCode })
            .HasPrincipalKey(item => new { item.Type, item.Code })
            .OnDelete(DeleteBehavior.Restrict);
        order.HasOne<StaffUser>().WithMany().HasForeignKey(o => o.DiscontinuerId).OnDelete(DeleteBehavior.Restrict);
        // An order is replaced once, by one order, and replaces at most one.
        order.HasOne<Order>().WithOne().HasForeignKey<Order>(o => o.ReplacesOrderId).OnDelete(DeleteBehavior.Restrict);
        order.HasOne<Order>().WithOne().HasForeignKey<Order>(o => o.ReplacedByOrderId).OnDelete(DeleteBehavior.Restrict);
        // Read by patient: the chart's active orders, newest first, and the check for an active order of the same item.
        order.HasIndex(o => new { o.PatientId, o.SignedAt });
    }
}

public sealed class OrderCatalogItemConfiguration : IEntityTypeConfiguration<OrderCatalogItem>
{
    public void Configure(EntityTypeBuilder<OrderCatalogItem> item)
    {
        item.ToTable("order_catalog_items", table =>
            table.HasCheckConstraint("ck_order_catalog_items_type", $"type IN ({Codes.List(OrderType.All)})"));

        item.HasKey(i => i.Code);
        item.Property(i => i.Code).HasMaxLength(20);
        item.Property(i => i.Type).HasMaxLength(20);
        item.Property(i => i.Display).HasMaxLength(200);
        item.Property(i => i.DisplayKey).HasMaxLength(200).HasComputedColumnSql("lower(display)", stored: true);
        item.Property(i => i.StandardSystem).HasMaxLength(20);
        item.Property(i => i.StandardCode).HasMaxLength(20);
        // The order form's search as a clinician types, and its check of the name picked: a type's names by their start,
        // and by the words inside them (PostgreSQL's own text search), as for the clinical codes.
        item.HasIndex(i => new { i.Type, i.DisplayKey }).HasOperators(null!, "text_pattern_ops");
        item.HasIndex(i => i.Display).HasMethod("GIN").IsTsVectorExpressionIndex("simple");
        // The order form takes an item by what it says.
        item.HasIndex(i => new { i.Type, i.Display }).IsUnique();
        item.HasData(OrderCatalogItem.Seeded);
    }
}
