using BudgetAgent.Controllers;
using BudgetAgent.Services;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// ─── Services ──────────────────────────────────────────────────────────────

builder.Services.AddControllers();

// MongoDB setup
var connectionString = builder.Configuration["MongoConnectionString"] 
                    ?? "mongodb://localhost:27017/budgetdb";

var mongoUrl = new MongoUrl(connectionString);
var mongoClient = new MongoClient(mongoUrl);
var databaseName = mongoUrl.DatabaseName ?? "budgetdb";
var database = mongoClient.GetDatabase(databaseName);

builder.Services.AddSingleton<IMongoDatabase>(database);

builder.Services.AddHttpClient<GeminiService>();
builder.Services.AddHttpClient<OpenAIService>();
builder.Services.AddHttpClient<TwilioWebhookController>();
builder.Services.AddHttpClient<WhapiService>();

builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<GeminiService>();
builder.Services.AddScoped<OpenAIService>();
builder.Services.AddScoped<WhapiService>();

builder.Services.AddScoped<ILlmService>(provider => 
{
    var config = provider.GetRequiredService<IConfiguration>();
    var llmProvider = config["LLM:Provider"] ?? config["LLM__Provider"] ?? "Gemini";
    
    switch (llmProvider.ToLower())
    {
        case "openai":
            return provider.GetRequiredService<OpenAIService>();
        case "gemini":
        default:
            return provider.GetRequiredService<GeminiService>();
    }
});

// ─── App ───────────────────────────────────────────────────────────────────

var app = builder.Build();

app.UseRouting();
app.MapControllers();

// Ensure Mongo indexes on startup
using (var scope = app.Services.CreateScope())
{
    var budget = scope.ServiceProvider.GetRequiredService<BudgetService>();
    budget.EnsureIndexes();
}

// Railway injects PORT env var; fall back to 8080
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");
