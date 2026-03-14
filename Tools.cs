using System.Text.Json;

namespace AzureAIAgent101;

/// <summary>
/// Tool implementations and helper methods.
/// </summary>
internal static class Tools
{
    public static string LookupKpi(string metric)
    {
        string m = (metric ?? "").Trim().ToUpperInvariant();
        return m switch
        {
            "MRR" => "MRR (Monthly Recurring Revenue): Sum of normalized monthly subscription revenue. Ex: sum(plan_price * seats).",
            "NPS" => "NPS (Net Promoter Score): %Promoters - %Detractors from survey responses.",
            _ => "Unknown metric. Try MRR or NPS."
        };
    }

    // ===== Video Helper Methods =====
    public static async Task<string?> PollVideoStatusAsync(HttpClient httpVid, string statusUrl, string apiKey, string jobId, Spinner spinner)
    {
        var pollSw = System.Diagnostics.Stopwatch.StartNew();
        while (pollSw.Elapsed < TimeSpan.FromMinutes(5))
        {
            await Task.Delay(5000);
            spinner.Update($"polling job {jobId}…");
            
            var statusReq = new HttpRequestMessage(HttpMethod.Get, statusUrl);
            statusReq.Headers.Add("Api-key", apiKey);
            using var statusResp = await httpVid.SendAsync(statusReq);
            var statusBody = await statusResp.Content.ReadAsStringAsync();
            
            if (!statusResp.IsSuccessStatusCode)
                return "failed";

            using var statusDoc = JsonDocument.Parse(statusBody);
            var status = statusDoc.RootElement.TryGetProperty("status", out var stEl)
                ? stEl.GetString() ?? "unknown" : "unknown";
            
            if (status is "succeeded" or "failed" or "cancelled")
                return status;
        }
        return "timeout";
    }

    public static async Task<string?> GetVideoGenerationIdAsync(HttpClient httpVid, string statusUrl, string apiKey, string jobId)
    {
        var statusReq = new HttpRequestMessage(HttpMethod.Get, statusUrl);
        statusReq.Headers.Add("Api-key", apiKey);
        using var statusResp = await httpVid.SendAsync(statusReq);
        var statusBody = await statusResp.Content.ReadAsStringAsync();
        
        if (!statusResp.IsSuccessStatusCode)
            return null;

        using var statusDoc = JsonDocument.Parse(statusBody);
        var generations = statusDoc.RootElement.TryGetProperty("generations", out var gensEl) 
            && gensEl.ValueKind == JsonValueKind.Array ? gensEl : default;
        
        if (generations.ValueKind != JsonValueKind.Array || generations.GetArrayLength() == 0)
            return null;

        var genId = generations[0].TryGetProperty("id", out var genIdEl) ? genIdEl.GetString() : null;
        return genId;
    }

    public static async Task<string?> DownloadVideoAsync(HttpClient httpVid, string baseUrl, string genId, string apiVersion, 
        string prompt, string apiKey, int width, int height, int duration)
    {
        string contentUrl = baseUrl + $"video/generations/{genId}/content/video?api-version={apiVersion}";
        var contentReq = new HttpRequestMessage(HttpMethod.Get, contentUrl);
        contentReq.Headers.Add("Api-key", apiKey);
        
        using var contentResp = await httpVid.SendAsync(contentReq);
        if (!contentResp.IsSuccessStatusCode)
        {
            var bodyFail = await contentResp.Content.ReadAsStringAsync();
            return JsonSerializer.Serialize(new { error = "video_download_failed", 
                status = (int)contentResp.StatusCode, body = bodyFail });
        }

        var videoBytes = await contentResp.Content.ReadAsByteArrayAsync();
        var filePath = $"video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
        await File.WriteAllBytesAsync(filePath, videoBytes);

        var htmlPath = Path.ChangeExtension(filePath, ".html");
        var html = $"<html><head><title>Video Playground</title></head><body style='background:#111;color:#eee;font-family:sans-serif'>" +
                   $"<h3>Prompt</h3><pre style='white-space:pre-wrap'>{System.Web.HttpUtility.HtmlEncode(prompt)}</pre>" +
                   "<h3>Video</h3>" +
                   $"<video src='{Path.GetFileName(filePath)}' controls autoplay style='max-width:100%;border:1px solid #444'></video>" +
                   "</body></html>";
        await File.WriteAllTextAsync(htmlPath, html, System.Text.Encoding.UTF8);

        return JsonSerializer.Serialize(new { video = filePath, playground = htmlPath, width, height, duration });
    }

    // ===== Weather API Client =====
    public sealed class OpenMeteoWeatherClient
    {
        private readonly HttpClient _http;
        public OpenMeteoWeatherClient(HttpClient http) => _http = http;

        public async Task<(double tempC, string resolvedName)> GetCurrentTempAsync(string location)
        {
            // 1) Geocode
            var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(location)}&count=1";
            using var geoRes = await _http.GetAsync(geoUrl);
            geoRes.EnsureSuccessStatusCode();
            using var geoDoc = JsonDocument.Parse(await geoRes.Content.ReadAsStringAsync());
            if (!geoDoc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                throw new InvalidOperationException("Location not found");

            var first = results[0];
            double lat = first.GetProperty("latitude").GetDouble();
            double lon = first.GetProperty("longitude").GetDouble();

            string name = first.GetProperty("name").GetString() ?? location;
            string admin1 = first.TryGetProperty("admin1", out var a1) ? a1.GetString() ?? "" : "";
            string country = first.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
            string resolved = string.Join(", ", new[] { name, admin1, country }.Where(s => !string.IsNullOrWhiteSpace(s)));

            // 2) Current weather
            var wxUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true";
            using var wxRes = await _http.GetAsync(wxUrl);
            wxRes.EnsureSuccessStatusCode();
            using var wxDoc = JsonDocument.Parse(await wxRes.Content.ReadAsStringAsync());
            double tempC = wxDoc.RootElement.GetProperty("current_weather").GetProperty("temperature").GetDouble();

            return (tempC, resolved);
        }
    }
}
