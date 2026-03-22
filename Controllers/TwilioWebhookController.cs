using BudgetAgent.Services;
using Microsoft.AspNetCore.Mvc;

namespace BudgetAgent.Controllers;

/// <summary>
/// Twilio sends a POST to this endpoint every time the user sends a WhatsApp message.
/// We process it with Claude and respond with TwiML (Twilio Markup Language).
/// </summary>
[ApiController]
[Route("api/webhook")]
public class TwilioWebhookController : ControllerBase
{
    private readonly GeminiService _gemini;
    private readonly ILogger<TwilioWebhookController> _logger;
    private readonly HttpClient _http;

    public TwilioWebhookController(GeminiService gemini, ILogger<TwilioWebhookController> logger, HttpClient http)
    {
        _gemini = gemini;
        _logger = logger;
        _http = http;
    }

    // POST /api/webhook/whatsapp
    [HttpPost("whatsapp")]
    public async Task<ContentResult> WhatsApp()
    {
        try
        {
            var form = await Request.ReadFormAsync();

            // Twilio sends the sender as "whatsapp:+911234567890"
            var from  = form["From"].ToString();
            var body  = form["Body"].ToString().Trim();
            var phone = from.Replace("whatsapp:", "").Trim();

            _logger.LogInformation("[IN]  {Phone}: {Body}", phone, body);

            // Extract image if available
            int.TryParse(form["NumMedia"].ToString(), out int numMedia);
            byte[]? imageBytes = null;
            string? mimeType = null;

            if (numMedia > 0)
            {
                var mediaUrl = form["MediaUrl0"].ToString();
                mimeType = form["MediaContentType0"].ToString();

                if (!string.IsNullOrEmpty(mediaUrl) && mimeType.StartsWith("image/"))
                {
                    _logger.LogInformation("Downloading image from {MediaUrl}", mediaUrl);
                    imageBytes = await _http.GetByteArrayAsync(mediaUrl);
                }
            }

            if (string.IsNullOrWhiteSpace(body) && imageBytes == null)
                return TwiML("I didn't catch that — please send a message or an image! 😊");

            var reply = await _gemini.ProcessMessage(phone, body, imageBytes, mimeType);

            _logger.LogInformation("[OUT] {Phone}: {Reply}", phone, reply);
            return TwiML(reply);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WhatsApp message");
            return TwiML("Sorry, something went wrong on my end! Please try again in a moment. 🔧");
        }
    }

    // GET /health  — used by Railway / Render health checks
    [HttpGet("/health")]
    public IActionResult Health() => Ok(new { status = "ok", timestamp = DateTime.UtcNow });

    // ─── Helper ────────────────────────────────────────────────────────────

    private ContentResult TwiML(string message)
    {
        // Escape XML special characters so Twilio doesn't choke on ₹ symbols etc.
        var safe = message
            .Replace("&",  "&amp;")
            .Replace("<",  "&lt;")
            .Replace(">",  "&gt;")
            .Replace("\"", "&quot;");

        return Content(
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Response>
                <Message>{safe}</Message>
            </Response>
            """,
            "application/xml");
    }
}
