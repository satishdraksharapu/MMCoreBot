# ── Stage 1: Build ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY BudgetAgent.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish

# ── Stage 2: Runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Create the data directory for SQLite (map Railway volume here)
RUN mkdir -p /data

COPY --from=build /app/publish .

# Railway sets PORT automatically; our app reads it in Program.cs
EXPOSE 8080

ENTRYPOINT ["dotnet", "BudgetAgent.dll"]
