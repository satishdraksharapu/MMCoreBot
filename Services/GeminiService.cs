using BudgetAgent.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BudgetAgent.Services;

/// <summary>
/// Orchestrates conversations with the Gemini API.
/// Uses Gemini's function-calling feature so the AI can record transactions
/// and update budgets directly in the database during a conversation turn.
///
/// Flow:
///   1. Build system instructions with current budget state
///   2. Send user message + conversation history to Gemini
///   3. If Gemini calls a function → execute it → send result back → get final reply
///   4. Return the final text to the WhatsApp controller
/// </summary>
public class GeminiService
{
    private readonly HttpClient _http;
    private readonly BudgetService _budget;
    private readonly IConfiguration _config;

    private const string ApiUrlBase = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-lite:generateContent";
    private readonly string _apiKey;

    public GeminiService(HttpClient http, BudgetService budget, IConfiguration config)
    {
        _http   = http;
        _budget = budget;
        _config = config;

        _apiKey = config["Gemini__ApiKey"]
               ?? config["Gemini:ApiKey"]
               ?? throw new InvalidOperationException("Gemini API key not configured. Set env var Gemini__ApiKey.");
    }

    // ─── Public Entry Point ────────────────────────────────────────────────

    public async Task<string> ProcessMessage(string phone, string userMessage, byte[]? imageBytes = null, string? mimeType = null)
    {
        // 1. Gather current state from DB
        var (budget, spent, remaining) = await _budget.GetBudgetSummary(phone);
        var transactions               = await _budget.GetMonthlyTransactions(phone);
        var categoryBreakdown          = await _budget.GetCategoryBreakdown(phone);
        var history                    = await _budget.GetRecentConversation(phone, limit: 10);

        // 2. Build inputs for Gemini
        var systemPrompt = BuildSystemPrompt(budget, spent, remaining, transactions, categoryBreakdown);
        var messages     = BuildMessages(history, userMessage, imageBytes, mimeType);
        var tools        = BuildTools();

        // 3. Call Gemini (handles tool-use loop internally)
        var reply = await CallGemini(systemPrompt, messages, tools, phone);

        // 4. Persist conversation
        var storedMessage = string.IsNullOrWhiteSpace(userMessage) 
            ? "[Image processed]" 
            : (imageBytes != null ? $"{userMessage}\n[Image processed]" : userMessage);

        await _budget.SaveMessage(phone, "user",      storedMessage);
        await _budget.SaveMessage(phone, "assistant", reply);

        return reply;
    }

    // ─── System Prompt ─────────────────────────────────────────────────────

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

    // ─── Messages Builder ──────────────────────────────────────────────────

    private static List<object> BuildMessages(List<ConversationMessage> history, string newUserMessage, byte[]? imageBytes, string? mimeType)
    {
        var messages = new List<object>();

        foreach (var m in history)
        {
            var role = m.Role == "assistant" ? "model" : "user";
            messages.Add(new { role = role, parts = new[] { new { text = m.Content } } });
        }

        var newParts = new List<object>();
        
        if (imageBytes != null && mimeType != null)
        {
            newParts.Add(new
            {
                inlineData = new
                {
                    mimeType = mimeType,
                    data = Convert.ToBase64String(imageBytes)
                }
            });
        }

        var textContent = string.IsNullOrWhiteSpace(newUserMessage) 
            ? "Please look at this image and extract any transaction details if applicable." 
            : newUserMessage;

        newParts.Add(new { text = textContent });

        messages.Add(new { role = "user", parts = newParts.ToArray() });
        return messages;
    }

    // ─── Tool Definitions ──────────────────────────────────────────────────

    private static object BuildTools() => new
    {
        functionDeclarations = new object[]
        {
            new
            {
                name = "set_monthly_budget",
                description = "Set or update the user's monthly budget.",
                parameters = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        amount = new { type = "NUMBER", description = "Monthly budget in INR (e.g. 30000)" }
                    },
                    required = new[] { "amount" }
                }
            },
            new
            {
                name = "add_transaction",
                description = "Record an expense or income entry. Call this whenever the user mentions spending or receiving money.",
                parameters = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        amount      = new { type = "NUMBER",  description = "Amount in INR" },
                        category    = new { type = "STRING",  description = "One of: Food, Transport, Shopping, Bills, Entertainment, Healthcare, Savings, Other" },
                        description = new { type = "STRING",  description = "Short description of what the money was for" },
                        type        = new { type = "STRING",  description = "Transaction type, e.g., expense or income" }
                    },
                    required = new[] { "amount", "category", "description", "type" }
                }
            }
        }
    };

    // ─── Gemini API Call (with tool-use loop) ──────────────────────────────

    private async Task<string> CallGemini(
        string systemPrompt, List<object> messages, object tools, string phone)
    {
        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = messages,
            tools = new[] { tools }
        };

        var url = $"{ApiUrlBase}?key={_apiKey}";
        var (responseJson, statusCode) = await PostToApi(url, payload);

        if (!statusCode.IsSuccessStatusCode)
            throw new Exception($"Gemini API error {statusCode}: {responseJson}");

        var result = JObject.Parse(responseJson);
        var candidates = result["candidates"] as JArray;
        
        if (candidates == null || candidates.Count == 0)
            return "Sorry, I couldn't process that.";

        var firstCandidate = candidates[0];
        var parts = firstCandidate["content"]?["parts"] as JArray ?? new JArray();

        // Check if there are function calls
        var functionCalls = parts.Where(p => p["functionCall"] != null).ToList();

        // ── Tool-use branch ──────────────────────────────────────────────
        if (functionCalls.Any())
        {
            var functionResponses = new List<object>();

            foreach (var call in functionCalls)
            {
                var functionCall = call["functionCall"];
                var toolName = functionCall?["name"]?.ToString() ?? "";
                var toolArgs = (functionCall?["args"] as JObject) ?? new JObject();

                var resultText = await ExecuteTool(phone, toolName, toolArgs);

                functionResponses.Add(new
                {
                    functionResponse = new
                    {
                        name = toolName,
                        response = new { result = resultText }
                    }
                });
            }

            // Append model turn + tool results, then ask Gemini for final reply
            messages.Add(new { role = "model", parts = parts });
            messages.Add(new { role = "user",  parts = functionResponses });

            var followUpPayload = new
            {
                systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = messages,
                tools = new[] { tools }
            };

            var (followJson, followStatus) = await PostToApi(url, followUpPayload);

            if (!followStatus.IsSuccessStatusCode)
                throw new Exception($"Gemini follow-up error {followStatus}: {followJson}");

            result = JObject.Parse(followJson);
            candidates = result["candidates"] as JArray;

            if (candidates == null || candidates.Count == 0)
                return "Processing complete.";

            firstCandidate = candidates[0];
            parts = firstCandidate["content"]?["parts"] as JArray ?? new JArray();
        }

        // ── Extract text from content blocks ─────────────────────────────
        return string.Join("\n", parts
            .Where(p => p["text"] != null)
            .Select(p => p["text"]?.ToString() ?? ""))
            .Trim();
    }

    // ─── Tool Executor ─────────────────────────────────────────────────────

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

                var (totalBudget, spent, remaining) = await _budget.GetBudgetSummary(phone);
                return $"Recorded: ₹{amount:N0} — {description} [{category}]. " +
                       $"Total spent: ₹{spent:N0}. Remaining: ₹{remaining:N0}.";
            }

            default:
                return $"Unknown tool: {toolName}";
        }
    }

    // ─── HTTP Helper ───────────────────────────────────────────────────────

    private async Task<(string Body, System.Net.Http.HttpResponseMessage Response)> PostToApi(string url, object payload)
    {
        var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        var json     = JsonConvert.SerializeObject(payload, settings);
        var content  = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content);
        var body     = await response.Content.ReadAsStringAsync();
        return (body, response);
    }
}
