namespace BudgetAgent.Services;

public interface ILlmService
{
    Task<string> ProcessMessage(string phone, string userMessage, byte[]? imageBytes = null, string? mimeType = null);
}
