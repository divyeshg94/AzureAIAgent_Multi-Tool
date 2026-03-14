using System.Text.Json;
using Azure.AI.OpenAI;
using OpenAI.Images;
using OpenAI.Chat;
using Microsoft.Extensions.Configuration;
using System.ClientModel;

namespace AzureAIAgent101;

/// <summary>
/// Context containing state needed by tool handlers.
/// </summary>
internal record class ToolContext(
    string SpeechModel,
    string VideoModel,
    string InferenceBase,
    string ProjectEndpoint,
    Tools.OpenMeteoWeatherClient WeatherClient
);

/// <summary>
/// Tool handlers for agent tool calls.
/// </summary>
internal static class ToolHandlers
{
    public static async Task<string> HandleKpiToolAsync(ChatToolCall call, Spinner spinner, ToolContext context)
    {
        using var args = JsonDocument.Parse(call.FunctionArguments.ToString());
        string metric = args.RootElement.TryGetProperty("metric", out var m)
                        ? m.GetString() ?? "MRR" : "MRR";
        spinner.Update($"calling lookup_kpi(\"{metric}\")…");
        
        using (var span = AzureAIAgent.Security.AgentTelemetry.StartToolCall("KpiTool", metric))
        {
            var output = Tools.LookupKpi(metric);
            span?.SetTag("tool.result.length", output.Length);
            return output;
        }
    }

    public static async Task<string> HandleWeatherToolAsync(ChatToolCall call, Spinner spinner, ToolContext context)
    {
        using var args = JsonDocument.Parse(call.FunctionArguments.ToString());
        if (!args.RootElement.TryGetProperty("location", out var locEl))
            return "{\"error\":\"missing_location\"}";

        string location = locEl.GetString() ?? "";
        string unit = args.RootElement.TryGetProperty("unit", out var uEl)
                      ? (uEl.GetString() ?? "c") : "c";

        spinner.Update($"calling get_weather(\"{location}\", \"{unit}\")…");
        
        using (var span = AzureAIAgent.Security.AgentTelemetry.StartToolCall("WeatherTool", location))
        {
            var (tempC, resolved) = await context.WeatherClient.GetCurrentTempAsync(location);
            double value = unit.Equals("f", StringComparison.OrdinalIgnoreCase)
                ? (tempC * 9.0 / 5.0 + 32.0) : tempC;

            var output = JsonSerializer.Serialize(new
            {
                location = resolved,
                temperature = Math.Round(value, 1),
                unit = unit.Equals("f", StringComparison.OrdinalIgnoreCase) ? "°F" : "°C",
                source = "open-meteo"
            });
            
            span?.SetTag("tool.result.location", resolved);
            span?.SetTag("tool.result.temperature", Math.Round(value, 1));
            return output;
        }
    }

    public static async Task<string> HandleImageToolAsync(ChatToolCall call, Spinner spinner, ToolContext context)
    {
        using var args = JsonDocument.Parse(call.FunctionArguments.ToString());
        var imagePrompt = args.RootElement.GetProperty("prompt").GetString() ?? "";
        
        using (var span = AzureAIAgent.Security.AgentTelemetry.StartToolCall("ImageTool", imagePrompt))
        {
            var imageClient = await GetImageClientAsync(context);
            var result = await imageClient.GenerateImageAsync(imagePrompt);
            var filePath = $"image_{DateTime.Now:HHmmss}.png";
            await File.WriteAllBytesAsync(filePath, result.Value.ImageBytes.ToArray());
            
            var output = JsonSerializer.Serialize(new { image = filePath, source = "gpt-image-1-mini" });
            span?.SetTag("tool.result.file", filePath);
            return output;
        }
    }

    public static async Task<string> HandleSpeechToolAsync(ChatToolCall call, Spinner spinner, ToolContext context)
    {
        using var args = JsonDocument.Parse(call.FunctionArguments.ToString());
        var text = args.RootElement.GetProperty("text").GetString() ?? string.Empty;
        
        if (string.IsNullOrWhiteSpace(context.SpeechModel))
            return "{\"error\":\"speech_not_configured\"}";
        
        var apiKey = GetConfigValue("AZURE_OPENAI_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return "{\"error\":\"no_api_key_for_speech\"}";

        spinner.Update("calling speech API…");
        try
        {
            using (var span = AzureAIAgent.Security.AgentTelemetry.StartToolCall("SpeechTool", text))
            {
                var payload = new
                {
                    model = context.SpeechModel,
                    voice = "alloy",
                    input = text,
                    format = "wav"
                };
                var json = JsonSerializer.Serialize(payload);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                var req = new HttpRequestMessage(HttpMethod.Post, context.InferenceBase + "audio/speech")
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
                req.Headers.Add("api-key", apiKey);
                using var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    var errTxt = await resp.Content.ReadAsStringAsync();
                    return JsonSerializer.Serialize(new { error = "speech_failed", status = (int)resp.StatusCode, body = errTxt });
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                var path = $"narration_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
                await File.WriteAllBytesAsync(path, bytes);
                var output = JsonSerializer.Serialize(new { audio = path, source = context.SpeechModel });
                
                span?.SetTag("tool.result.file", path);
                return output;
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "speech_exception", message = ex.Message });
        }
    }

    public static async Task<string> HandleVideoToolAsync(ChatToolCall call, Spinner spinner, ToolContext context)
    {
        using var args = JsonDocument.Parse(call.FunctionArguments.ToString());
        
        if (string.IsNullOrWhiteSpace(context.VideoModel))
            return "{\"error\":\"video_not_configured\"}";
        
        var apiKey = GetConfigValue("Azure_Video_Key");
        if (string.IsNullOrWhiteSpace(apiKey))
            return "{\"error\":\"no_api_key_for_video\"}";

        var prompt = args.RootElement.GetProperty("prompt").GetString() ?? string.Empty;
        int duration = 5;
        if (args.RootElement.TryGetProperty("durationSeconds", out var dEl))
            duration = Math.Clamp(dEl.GetInt32(), 1, 10);

        int width = 480, height = 480;
        var apiVersion = "preview";

        spinner.Update("creating video job…");
        try
        {
            // Ensure base ends with /openai/v1/
            string baseUrl = context.InferenceBase.EndsWith("/") ? context.InferenceBase : context.InferenceBase + "/";
            if (!baseUrl.Contains("/openai/v1/", StringComparison.OrdinalIgnoreCase))
                baseUrl = (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/") + "openai/v1/";

            string jobsUrl = baseUrl + $"video/generations/jobs?api-version={apiVersion}";
            var payload = new { prompt, width, height, n_seconds = duration, model = context.VideoModel };
            var json = JsonSerializer.Serialize(payload);
            
            using var httpVid = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var createReq = new HttpRequestMessage(HttpMethod.Post, jobsUrl)
            { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            createReq.Headers.Add("Api-key", apiKey);
            
            using var createResp = await httpVid.SendAsync(createReq);
            var createBody = await createResp.Content.ReadAsStringAsync();
            if (!createResp.IsSuccessStatusCode)
                return JsonSerializer.Serialize(new { error = "video_create_failed", status = (int)createResp.StatusCode, body = createBody });

            using var createDoc = JsonDocument.Parse(createBody);
            var jobId = createDoc.RootElement.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(jobId))
                return JsonSerializer.Serialize(new { error = "missing_job_id", body = createBody });

            // Poll job status
            spinner.Update($"job {jobId} created; polling…");
            string statusUrl = baseUrl + $"video/generations/jobs/{jobId}?api-version={apiVersion}";
            var status = await Tools.PollVideoStatusAsync(httpVid, statusUrl, apiKey, jobId, spinner) ?? "failed";
            
            if (status != "succeeded")
                return JsonSerializer.Serialize(new { error = "job_not_succeeded", job = jobId, finalStatus = status });

            // Get job results and download video
            var genId = await Tools.GetVideoGenerationIdAsync(httpVid, statusUrl, apiKey, jobId);
            if (string.IsNullOrWhiteSpace(genId))
                return JsonSerializer.Serialize(new { error = "missing_generation_id", job = jobId });

            var videoResult = await Tools.DownloadVideoAsync(httpVid, baseUrl, genId, apiVersion, prompt, apiKey, width, height, duration);
            return videoResult ?? JsonSerializer.Serialize(new { error = "download_failed" });
        }
        catch (Exception vx)
        {
            return JsonSerializer.Serialize(new { error = "video_exception", message = vx.Message });
        }
    }

    // ===== Helpers =====
    private static string? GetConfigValue(string key)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
           .AddJsonFile("appsettings.json", optional: true)
           .AddUserSecrets<Program>(optional: true)
           .Build();
        return config[key];
    }

    private static async Task<ImageClient> GetImageClientAsync(ToolContext context)
    {
        var apiKey = GetConfigValue("AZURE_OPENAI_KEY");
        var deployment = GetConfigValue("Image_Model");
        
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(context.ProjectEndpoint))
        {
            throw new InvalidOperationException("Image generation requires AZURE_OPENAI_KEY and projectEndpoint configuration.");
        }
        
        var u = new Uri(context.ProjectEndpoint);
        var baseUrl = $"{u.Scheme}://{u.Host}/openai/v1/";
        return new ImageClient(model: deployment, credential: new System.ClientModel.ApiKeyCredential(apiKey), 
            options: new OpenAI.OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
    }
}
