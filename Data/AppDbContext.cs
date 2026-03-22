using BudgetAgent.Models;
using Microsoft.EntityFrameworkCore;

namespace BudgetAgent.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<UserBudget> UserBudgets { get; set; }
    public DbSet<Transaction> Transactions { get; set; }
    public DbSet<ConversationMessage> ConversationMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Ensure one budget entry per user per month
        modelBuilder.Entity<UserBudget>()
            .HasIndex(u => new { u.PhoneNumber, u.Month, u.Year })
            .IsUnique();
    }
}
