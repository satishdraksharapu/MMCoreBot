using BudgetAgent.Data;
using BudgetAgent.Models;
using Microsoft.EntityFrameworkCore;

namespace BudgetAgent.Services;

/// <summary>
/// Handles all database operations: budgets, transactions, conversation history.
/// </summary>
public class BudgetService
{
    private readonly AppDbContext _db;

    public BudgetService(AppDbContext db) => _db = db;

    // ─── Budget ────────────────────────────────────────────────────────────

    public async Task<UserBudget?> GetCurrentBudget(string phone)
    {
        var now = DateTime.UtcNow;
        return await _db.UserBudgets
            .FirstOrDefaultAsync(b => b.PhoneNumber == phone
                                   && b.Month == now.Month
                                   && b.Year == now.Year);
    }

    public async Task<UserBudget> SetBudget(string phone, decimal amount)
    {
        var now = DateTime.UtcNow;
        var budget = await GetCurrentBudget(phone);

        if (budget == null)
        {
            budget = new UserBudget
            {
                PhoneNumber = phone,
                MonthlyBudget = amount,
                Month = now.Month,
                Year = now.Year
            };
            _db.UserBudgets.Add(budget);
        }
        else
        {
            budget.MonthlyBudget = amount;
            budget.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return budget;
    }

    // ─── Transactions ──────────────────────────────────────────────────────

    public async Task<Transaction> AddTransaction(
        string phone, decimal amount, string category, string description, string type = "expense")
    {
        var now = DateTime.UtcNow;
        var tx = new Transaction
        {
            PhoneNumber = phone,
            Amount = amount,
            Category = category,
            Description = description,
            Type = type,
            Month = now.Month,
            Year = now.Year
        };
        _db.Transactions.Add(tx);
        await _db.SaveChangesAsync();
        return tx;
    }

    public async Task<List<Transaction>> GetMonthlyTransactions(string phone)
    {
        var now = DateTime.UtcNow;
        return await _db.Transactions
            .Where(t => t.PhoneNumber == phone && t.Month == now.Month && t.Year == now.Year)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    /// <summary>Returns (totalBudget, totalSpent, remaining) for the current month.</summary>
    public async Task<(decimal Budget, decimal Spent, decimal Remaining)> GetBudgetSummary(string phone)
    {
        var budget = await GetCurrentBudget(phone);
        var transactions = await GetMonthlyTransactions(phone);

        decimal totalBudget = budget?.MonthlyBudget ?? 0;
        decimal spent = transactions.Where(t => t.Type == "expense").Sum(t => t.Amount);
        decimal income = transactions.Where(t => t.Type == "income").Sum(t => t.Amount);
        decimal remaining = totalBudget - spent + income;

        return (totalBudget, spent, remaining);
    }

    public async Task<Dictionary<string, decimal>> GetCategoryBreakdown(string phone)
    {
        var transactions = await GetMonthlyTransactions(phone);
        return transactions
            .Where(t => t.Type == "expense")
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
    }

    // ─── Conversation History ──────────────────────────────────────────────

    /// <summary>Retrieves the last N messages for a user, oldest first (for Claude's messages array).</summary>
    public async Task<List<ConversationMessage>> GetRecentConversation(string phone, int limit = 10)
    {
        return await _db.ConversationMessages
            .Where(m => m.PhoneNumber == phone)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .OrderBy(m => m.CreatedAt)   // re-sort ascending for Claude
            .ToListAsync();
    }

    public async Task SaveMessage(string phone, string role, string content)
    {
        _db.ConversationMessages.Add(new ConversationMessage
        {
            PhoneNumber = phone,
            Role = role,
            Content = content
        });
        await _db.SaveChangesAsync();
    }
}
