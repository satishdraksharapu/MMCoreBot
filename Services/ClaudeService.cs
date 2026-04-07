using System.Net.Http.Headers;
using BudgetAgent.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BudgetAgent.Services;

public class ClaudeService : ILlmService
{
    private readonly HttpClient _http;
    private readonly BudgetService _budget;
    private readonly IConfiguration _config;
    private readonly string _apiKey;
    private readonly string _modelToUse;

    private const string ApiUrlBase = "https://api.anthropic.com/v1/messages";

    public ClaudeService(HttpClient http, BudgetService budget, IConfiguration config)
    {
        _http = http;
        _budget = budget;
        _config = config;

        _apiKey = config["Claude__ApiKey"]
               ?? config["Claude:ApiKey"]
               ?? throw new InvalidOperationException("Claude API key not configured. Set env var Claude__ApiKey.");

        _modelToUse = config["Claude__ModelToUse"]
               ?? config["Claude:ModelToUse"]
               ?? "claude-3-haiku-20240307";

        _http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<string> ProcessMessage(string phone, string userMessage, byte[]? imageBytes = null, string? mimeType = null)
    {
        var (budget, spent, remaining) = await _budget.GetBudgetSummary(phone);
        var transactions = await _budget.GetMonthlyTransactions(phone);
        var categoryBreakdown = await _budget.GetCategoryBreakdown(phone);
        var history = await _budget.GetRecentConversation(phone, limit: 10);

        var systemPrompt = BuildSystemPrompt(budget, spent, remaining, transactions, categoryBreakdown);
        var messages = BuildMessages(history, userMessage, imageBytes, mimeType);
        var tools = BuildTools();

        var reply = await CallClaude(systemPrompt, messages, tools, phone);

        var storedMessage = string.IsNullOrWhiteSpace(userMessage)
            ? "[Image processed]"
            : (imageBytes != null ? $"{userMessage}\n[Image processed]" : userMessage);

        await _budget.SaveMessage(phone, "user", storedMessage);
        await _budget.SaveMessage(phone, "assistant", reply);

        return reply;
    }

    private static string BuildSystemPrompt(
        decimal budget, decimal spent, decimal remaining,
        List<Transaction> transactions,
        Dictionary<string, decimal> categoryBreakdown)
    {
        var monthName = DateTime.UtcNow.ToString("MMMM yyyy");

        var txLines = transactions.Any()
            ? string.Join("\n", transactions.Take(10).Select(t =>
                $"  • {t.CreatedAt:MMM dd} | {t.Category,-15} | ₹{t.Amount,8:N0} | {t.Description}"))
            : "  No transactions yet this month.";

        var catLines = categoryBreakdown.Any()
            ? string.Join("\n", categoryBreakdown
                .OrderByDescending(c => c.Value)
                .Select(c => $"  • {c.Key,-15} ₹{c.Value:N0}"))
            : "  No spending data yet.";

        return $"""
            You are BudgetBot, a friendly WhatsApp budget assistant for Indian users.
            You help users track spending, stay within budget, and make smarter financial decisions.

            ── CURRENT MONTH: {monthName} ──────────────────────────────
            Monthly Budget : ₹{budget:N0}{(budget == 0 ? "  ⚠️ (not set yet)" : "")}
            Total Spent    : ₹{spent:N0}
            Remaining      : ₹{remaining:N0}
            Transactions   : {transactions.Count}

            ── SPENDING BY CATEGORY ────────────────────────────────────
            {catLines}

            ── RECENT TRANSACTIONS (latest 10) ─────────────────────────
            {txLines}
            ────────────────────────────────────────────────────────────

            TOOLS YOU HAVE:
              • set_monthly_budget — call this when user sets or changes their budget
              • add_transaction    — call this when user mentions any expense or income

            RESPONSE RULES (WhatsApp-friendly):
              1. Keep replies short and clear; use emojis sparingly.
              2. After adding a transaction, ALWAYS show: spent so far + remaining.
              3. Use ₹ for currency (Indian Rupees).
              4. Categories to use: Food, Transport, Shopping, Bills, Entertainment,
                 Healthcare, Savings, Other.
              5. If no budget is set and the user asks about tracking, ask them to set one first.
              6. For "hi" / "hello" / greetings → show a warm welcome + current budget status.
              7. For budget suggestions, analyse category breakdown and give practical advice.
              8. If budget set to 0 or no budget, don't show remaining calculation as it's meaningless.
            """;
    }

    private static List<object> BuildMessages(List<ConversationMessage> history, string newUserMessage, byte[]? imageBytes, string? mimeType)
    {
        var messages = new List<object>();

        foreach (var m in history)
        {
            messages.Add(new { role = m.Role, content = m.Content });
        }

        var textContent = string.IsNullOrWhiteSpace(newUserMessage)
            ? "Please look at this image and extract any transaction details if applicable."
            : newUserMessage;

        if (imageBytes != null && mimeType != null)
        {
            messages.Add(new
            {
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "image",
                        source = new
                        {
                            type = "base64",
                            media_type = mimeType,
                            data = Convert.ToBase64String(imageBytes)
                        }
                    },
                    new { type = "text", text = textContent }
                }
            });
        }
        else
        {
            messages.Add(new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = textContent }
                }
            });
        }

        return messages;
    }

    private static object[] BuildTools() => new object[]
    {
        new
        {
            name = "set_monthly_budget",
            description = "Set or update the user's monthly budget.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    amount = new { type = "number", description = "Monthly budget in INR (e.g. 30000)" }
                },
                required = new[] { "amount" }
            }
        },
        new
        {
            name = "add_transaction",
            description = "Record an expense or income entry. Call this whenever the user mentions spending or receiving money.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    amount      = new { type = "number", description = "Amount in INR" },
                    category    = new { type = "string", description = "One of: Food, Transport, Shopping, Bills, Entertainment, Healthcare, Savings, Other" },
                    description = new { type = "string", description = "Short description of what the money was for" },
                    type        = new { type = "string", description = "Transaction type, e.g., expense or income" }
                },
                required = new[] { "amount", "category", "description", "type" }
            }
        }
    };

    private async Task<string> CallClaude(string systemPrompt, List<object> messages, object[] tools, string phone)
    {
        var payload = new
        {
            model = _modelToUse,
            max_tokens = 1024,
            system = systemPrompt,
            messages = messages,
            tools = tools
        };

        var (responseJson, statusCode) = await PostToApi(ApiUrlBase, payload);

        if (!statusCode.IsSuccessStatusCode)
            throw new Exception($"Claude API error {statusCode}: {responseJson}");

        var result = JObject.Parse(responseJson);
        var contentBlocks = result["content"] as JArray;
        
        if (contentBlocks == null || contentBlocks.Count == 0)
            return "Sorry, I couldn't process that.";

        var toolUses = contentBlocks.Where(b => b["type"]?.ToString() == "tool_use").ToList();
        
        if (toolUses.Any())
        {
            messages.Add(new { role = "assistant", content = contentBlocks });

            var toolResultsContent = new List<object>();

            foreach (var toolUse in toolUses)
            {
                var toolUseId = toolUse["id"]?.ToString() ?? "";
                var toolName = toolUse["name"]?.ToString() ?? "";
                var toolArgs = toolUse["input"] as JObject ?? new JObject();

                var resultText = await ExecuteTool(phone, toolName, toolArgs);

                toolResultsContent.Add(new
                {
                    type = "tool_result",
                    tool_use_id = toolUseId,
                    content = resultText
                });
            }

            messages.Add(new { role = "user", content = toolResultsContent.ToArray() });

            var followUpPayload = new
            {
                model = _modelToUse,
                max_tokens = 1024,
                system = systemPrompt,
                messages = messages,
                tools = tools
            };

            var (followJson, followStatus) = await PostToApi(ApiUrlBase, followUpPayload);

            if (!followStatus.IsSuccessStatusCode)
                throw new Exception($"Claude follow-up error {followStatus}: {followJson}");

            result = JObject.Parse(followJson);
            contentBlocks = result["content"] as JArray;

            if (contentBlocks == null || contentBlocks.Count == 0)
                return "Processing complete.";
        }

        return string.Join("\n", contentBlocks
            .Where(b => b["type"]?.ToString() == "text")
            .Select(b => b["text"]?.ToString() ?? ""))
            .Trim();
    }

    private async Task<string> ExecuteTool(string phone, string toolName, JObject input)
    {
        switch (toolName)
        {
            case "set_monthly_budget":
            {
                var amount = input["amount"]!.Value<decimal>();
                await _budget.SetBudget(phone, amount);
                return $"Budget set to ₹{amount:N0} for this month.";
            }

            case "add_transaction":
            {
                var amount      = input["amount"]!.Value<decimal>();
                var category    = input["category"]?.ToString()    ?? "Other";
                var description = input["description"]?.ToString() ?? "";
                var type        = input["type"]?.ToString()        ?? "expense";

                await _budget.AddTransaction(phone, amount, category, description, type);

                var (_, spent, remaining) = await _budget.GetBudgetSummary(phone);
                return $"Recorded: ₹{amount:N0} — {description} [{category}]. " +
                       $"Total spent: ₹{spent:N0}. Remaining: ₹{remaining:N0}.";
            }

            default:
                return $"Unknown tool: {toolName}";
        }
    }

    private async Task<(string Body, System.Net.Http.HttpResponseMessage Response)> PostToApi(string url, object payload)
    {
        var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        var json = JsonConvert.SerializeObject(payload, settings);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();
        return (body, response);
    }
}
