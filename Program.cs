using BudgetAgent.Data;
using BudgetAgent.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ─── Services ──────────────────────────────────────────────────────────────

builder.Services.AddControllers();

// SQLite — path is /data/budget.db so Railway's persistent volume can map to /data
var connectionString = builder.Configuration["ConnectionStrings__DefaultConnection"]
                    ?? builder.Configuration.GetConnectionString("DefaultConnection")
                    ?? "Data Source=/data/budget.db";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddHttpClient<GeminiService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<GeminiService>();

// ─── App ───────────────────────────────────────────────────────────────────

var app = builder.Build();

// Auto-create database tables on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseRouting();
app.MapControllers();

// Railway injects PORT env var; fall back to 8080
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");
