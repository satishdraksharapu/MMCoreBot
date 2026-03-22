using System.Net.Http.Headers;
using Newtonsoft.Json;

namespace BudgetAgent.Services;

public class WhapiService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly string _token;
    private readonly ILogger<WhapiService> _logger;

    public WhapiService(HttpClient http, IConfiguration config, ILogger<WhapiService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        
        _token = _config["WhatsApp__WhapiToken"] ?? _config["WhatsApp:WhapiToken"] ?? "";
        
        if (!string.IsNullOrEmpty(_token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
    }

    public async Task<byte[]?> DownloadImageAsync(string url)
    {
        try
        {
            var response = await _http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }
            _logger.LogWarning("Failed to download image from Whapi: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading image from Whapi");
        }
        return null;
    }

    public async Task SendMessageAsync(string phone, string text)
    {
        if (string.IsNullOrEmpty(_token))
        {
            _logger.LogWarning("Cannot send Whapi message: WhapiToken is missing from configuration.");
            return;
        }

        var payload = new 
        {
            to = phone,
            body = text
        };

        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("https://gate.whapi.cloud/messages/text", content);
        
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            _logger.LogError("Whapi Send Error {StatusCode}: {Error}", response.StatusCode, err);
        }
    }
}
