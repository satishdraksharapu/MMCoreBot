using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BudgetAgent.Models;

/// <summary>Stores the monthly budget for each user (identified by WhatsApp phone number)</summary>
public class UserBudget
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = "";
    public decimal MonthlyBudget { get; set; }
    public int Month { get; set; }
    public int Year { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Stores each expense or income entry the user tells the agent</summary>
public class Transaction
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = "";
    public decimal Amount { get; set; }
    public string Category { get; set; } = "Other";     // Food, Transport, Bills, etc.
    public string Description { get; set; } = "";
    public string Type { get; set; } = "expense";       // "expense" or "income"
    public int Month { get; set; }
    public int Year { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Persists the last N messages per user so Claude has conversational context</summary>
public class ConversationMessage
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = "";
    public string Role { get; set; } = "";  // "user" or "assistant"
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
