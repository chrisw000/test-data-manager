using Microsoft.EntityFrameworkCore;

namespace Acme.Billing.Data.Infrastructure;

public class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<AccountModel> Accounts => Set<AccountModel>();
    public DbSet<InvoiceModel> Invoices => Set<InvoiceModel>();
    public DbSet<CustomerSummaryModel> CustomerSummaries => Set<CustomerSummaryModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountModel>(account =>
        {
            account.Property(a => a.Name).IsRequired().HasMaxLength(200);
            account.Property(a => a.Currency).HasMaxLength(3);
        });

        modelBuilder.Entity<InvoiceModel>(invoice =>
        {
            invoice.Property(i => i.Id).ValueGeneratedOnAdd();
            invoice.Property(i => i.InvoiceNumber).IsRequired().HasMaxLength(50);
            invoice.HasOne(i => i.Account)
                .WithMany()
                .HasForeignKey(i => i.AccountId);
            // CustomerId is deliberately NOT a mapped relationship: the principal lives in
            // another domain's database. It is populated via the identity contract.
        });
    }
}
