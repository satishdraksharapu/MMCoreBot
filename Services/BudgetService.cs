using BudgetAgent.Models;
using MongoDB.Driver;

namespace BudgetAgent.Services;

/// <summary>
/// Handles all database operations: budgets, transactions, conversation history.
/// </summary>
public class BudgetService
{
    private readonly IMongoCollection<UserBudget> _budgets;
    private readonly IMongoCollection<Transaction> _transactions;
    private readonly IMongoCollection<ConversationMessage> _messages;

    public BudgetService(IMongoDatabase database)
    {
        _budgets = database.GetCollection<UserBudget>("UserBudgets");
        _transactions = database.GetCollection<Transaction>("Transactions");
        _messages = database.GetCollection<ConversationMessage>("ConversationMessages");

        // Ensure one budget entry per user per month
        var indexKeysDefinition = Builders<UserBudget>.IndexKeys
            .Ascending(u => u.PhoneNumber)
            .Ascending(u => u.Month)
            .Ascending(u => u.Year);
        var indexOptions = new CreateIndexOptions { Unique = true };
        var indexModel = new CreateIndexModel<UserBudget>(indexKeysDefinition, indexOptions);
        _budgets.Indexes.CreateOne(indexModel);
    }

    // ─── Budget ────────────────────────────────────────────────────────────

    public async Task<UserBudget?> GetCurrentBudget(string phone)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<UserBudget>.Filter.And(
            Builders<UserBudget>.Filter.Eq(b => b.PhoneNumber, phone),
            Builders<UserBudget>.Filter.Eq(b => b.Month, now.Month),
            Builders<UserBudget>.Filter.Eq(b => b.Year, now.Year)
        );
        return await _budgets.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<UserBudget> SetBudget(string phone, decimal amount)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<UserBudget>.Filter.And(
            Builders<UserBudget>.Filter.Eq(b => b.PhoneNumber, phone),
            Builders<UserBudget>.Filter.Eq(b => b.Month, now.Month),
            Builders<UserBudget>.Filter.Eq(b => b.Year, now.Year)
        );

        var update = Builders<UserBudget>.Update
            .Set(b => b.MonthlyBudget, amount)
            .Set(b => b.UpdatedAt, DateTime.UtcNow)
            .SetOnInsert(b => b.CreatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<UserBudget>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };

        return await _budgets.FindOneAndUpdateAsync(filter, update, options);
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
        await _transactions.InsertOneAsync(tx);
        return tx;
    }

    public async Task<List<Transaction>> GetMonthlyTransactions(string phone)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<Transaction>.Filter.And(
            Builders<Transaction>.Filter.Eq(t => t.PhoneNumber, phone),
            Builders<Transaction>.Filter.Eq(t => t.Month, now.Month),
            Builders<Transaction>.Filter.Eq(t => t.Year, now.Year)
        );
        return await _transactions.Find(filter).SortByDescending(t => t.CreatedAt).ToListAsync();
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
        var filter = Builders<ConversationMessage>.Filter.Eq(m => m.PhoneNumber, phone);
        var messages = await _messages.Find(filter)
            .SortByDescending(m => m.CreatedAt)
            .Limit(limit)
            .ToListAsync();
        
        messages.Reverse(); // re-sort ascending for Claude/Gemini
        return messages;
    }

    public async Task SaveMessage(string phone, string role, string content)
    {
        await _messages.InsertOneAsync(new ConversationMessage
        {
            PhoneNumber = phone,
            Role = role,
            Content = content
        });
    }
}
