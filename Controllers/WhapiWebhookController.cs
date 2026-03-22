using BudgetAgent.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BudgetAgent.Controllers;

[ApiController]
[Route("api/webhook/whapi")]
public class WhapiWebhookController : ControllerBase
{
    private readonly GeminiService _gemini;
    private readonly WhapiService _whapi;
    private readonly IConfiguration _config;
    private readonly ILogger<WhapiWebhookController> _logger;

    public WhapiWebhookController(GeminiService gemini, WhapiService whapi, IConfiguration config, ILogger<WhapiWebhookController> logger)
    {
        _gemini = gemini;
        _whapi = whapi;
        _config = config;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        // 1. Check if Whapi is the active provider
        var provider = _config["WhatsApp__Provider"] ?? _config["WhatsApp:Provider"] ?? "Twilio";
        if (!provider.Equals("Whapi", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { status = "ignored", reason = "provider is not Whapi" });
        }

        try
        {
            using var reader = new System.IO.StreamReader(Request.Body);
            var jsonString = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(jsonString)) return Ok();

            var payload = JObject.Parse(jsonString);

            var messages = payload["messages"] as JArray;
            if (messages == null || messages.Count == 0) return Ok();

            foreach (var message in messages)
            {
                // Only process messages from users, not from ourselves
                var fromMe = message["from_me"]?.Value<bool>() ?? false;
                if (fromMe) continue;

                var from = message["from"]?.ToString();
                if (string.IsNullOrEmpty(from)) continue;

                // Strip the @s.whatsapp.net suffix to get the raw phone number
                var phone = from.Replace("@s.whatsapp.net", "").Replace("@c.us", "");

                // Extract text and images
                var type = message["type"]?.ToString();
                string body = "";
                byte[]? imageBytes = null;
                string? mimeType = null;

                if (type == "text")
                {
                    body = message["text"]?["body"]?.ToString() ?? "";
                }
                else if (type == "image")
                {
                    body = message["image"]?["caption"]?.ToString() ?? "";
                    var imageLink = message["image"]?["link"]?.ToString();
                    mimeType = message["image"]?["mime_type"]?.ToString() ?? "image/jpeg";

                    if (string.IsNullOrEmpty(imageLink))
                    {
                        var msgId = message["id"]?.ToString();
                        if (!string.IsNullOrEmpty(msgId))
                        {
                            imageLink = $"https://gate.whapi.cloud/messages/{msgId}/media";
                        }
                    }

                    if (!string.IsNullOrEmpty(imageLink))
                    {
                        imageBytes = await _whapi.DownloadImageAsync(imageLink);
                    }

                    if (imageBytes == null)
                    {
                        _logger.LogWarning("[IN WHAPI] Failed to download image for message ID {MsgId}. Proceeding with text only if available.", message["id"]);
                        if (string.IsNullOrWhiteSpace(body))
                        {
                            body = "[An image was sent but it could not be downloaded or processed by the system.]";
                        }
                    }
                }
                else 
                {
                    // Ignore unsupported message types (video, audio, etc) for now
                    continue;
                }

                _logger.LogInformation("[IN WHAPI] {Phone}: {Body} (Image bytes: {HasImage})", phone, body, imageBytes != null);

                if (string.IsNullOrWhiteSpace(body) && imageBytes == null)
                    continue;

                // 2. Process via Gemini
                var reply = await _gemini.ProcessMessage(phone, body, imageBytes, mimeType);

                _logger.LogInformation("[OUT WHAPI] {Phone}: {Reply}", phone, reply);

                // 3. Send reply via Whapi Service
                await _whapi.SendMessageAsync(from, reply); // Sends back to full @s.whatsapp.net sender automatically
            }

            return Ok(new { status = "ok" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Whapi webhook payload");
            // Always return OK to webhooks so they don't retry unnecessarily
            return Ok();
        }
    }
}
