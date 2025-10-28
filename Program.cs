using System.ClientModel;                 // ApiKeyCredential
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI;           // OpenAI v1 client options
using OpenAI.Chat;      // ChatClient, ChatMessage, ChatTool, etc.

internal class Program
{
    // ===== Console helpers =====
    static void WriteRole(string role, ConsoleColor color)
    {
        var old = Console.ForegroundColor; Console.ForegroundColor = color;
        Console.Write(role); Console.ForegroundColor = old;
    }
    static void WriteInfo(string text)
    {
        var old = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(text); Console.ForegroundColor = old;
    }
    static void WriteWarn(string text)
    {
        var old = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(text); Console.ForegroundColor = old;
    }
    static void WriteError(string text)
    {
        var old = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(text); Console.ForegroundColor = old;
    }
    static void WriteWrapped(string text, ConsoleColor color)
    {
        var width = Math.Max(40, Console.WindowWidth - 4);
        var old = Console.ForegroundColor; Console.ForegroundColor = color;
        foreach (var line in (text ?? string.Empty).Replace("\r", "").Split('\n'))
        {
            var remaining = line;
            while (remaining.Length > width)
            {
                var cut = remaining.LastIndexOf(' ', Math.Min(width, remaining.Length - 1));
                if (cut <= 0) cut = Math.Min(width, remaining.Length);
                Console.WriteLine(remaining[..cut]);
                remaining = remaining[cut..].TrimStart();
            }
            Console.WriteLine(remaining);
        }
        Console.ForegroundColor = old;
    }

    sealed class Spinner : IDisposable
    {
        private readonly object _lock = new();
        private string _text;
        private bool _running = true;
        private readonly Thread _thread;
        private readonly string[] _frames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        public Spinner(string text) { _text = text; _thread = new Thread(Run) { IsBackground=true }; _thread.Start(); }
        public void Update(string text) { lock (_lock) _text = text; }
        public void Done(string? final = null)
        {
            _running = false; Thread.Sleep(60);
            Console.Write("\r".PadRight(Console.WindowWidth - 1) + "\r");
            if (!string.IsNullOrWhiteSpace(final)) WriteInfo(final);
        }
        private void Run()
        {
            int i = 0; while (_running)
            {
                string t; lock (_lock) t=_text;
                Console.Write($"\r{_frames[i % _frames.Length]} {t}".PadRight(Console.WindowWidth - 1));
                i++; Thread.Sleep(80);
            }
        }
        public void Dispose() => Done();
    }

    // ===== SDK objects & state =====
    static ChatClient chat = null!;
    static OpenMeteoWeatherClient weatherClient = null!;
    static double modelTemperature = 0.2;
    static string defaultTempUnit = "c";
    static string systemPrompt =
        "You are a precise .NET research assistant. " +
        "Use tools when helpful. Use get_weather for any weather/temperature question. " +
        "If you call lookup_kpi, cite 'local-kb'. " +
        "Return: TL;DR (1 line), bullet points, and 'Sources' at the end.";
    static System.Collections.Generic.List<ChatMessage> history = new();

    // Tools
    static ChatTool lookupKpiTool = ChatTool.CreateFunctionTool(
        functionName: "lookup_kpi",
        functionDescription: "Given a KPI name, return its definition and a sample formula.",
        functionParameters: BinaryData.FromString(
            "{\"type\":\"object\",\"properties\":{\"metric\":{\"type\":\"string\",\"description\":\"KPI, e.g., 'MRR' or 'NPS'\"}},\"required\":[\"metric\"]}"
        ));

    static ChatTool getWeatherTool = ChatTool.CreateFunctionTool(
        functionName: "get_weather",
        functionDescription: "Get current air temperature for a place (city/town/area).",
        functionParameters: BinaryData.FromString(
            "{\"type\":\"object\",\"properties\":{" +
            "\"location\":{\"type\":\"string\",\"description\":\"City/town/place, e.g., 'Miami' or 'Chennai'\"}," +
            "\"unit\":{\"type\":\"string\",\"enum\":[\"c\",\"f\"],\"description\":\"Temperature unit; default 'c'\"}" +
            "},\"required\":[\"location\"]}"
        ));

    public static async Task Main()
    {
        // ===== Config =====
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<Program>(optional: true)
            .Build();

        var projectEndpoint = config["projectEndpoint"];   // e.g., https://...services.ai.azure.com/api/projects/firstProject
        var resourceEndpoint = config["endpoint"];         // e.g., https://<resource>.openai.azure.com/
        var deployment = config["model"];                  // DEPLOYMENT name
        if (string.IsNullOrWhiteSpace(deployment))
        {
            WriteError("Missing 'model' (deployment name) in config.");
            return;
        }

        // Build ChatClient for either Project or classic Resource mode
        chat = await CreateChatClientAsync(projectEndpoint, resourceEndpoint, deployment);

        // Other clients
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        weatherClient = new OpenMeteoWeatherClient(http);

        history = new System.Collections.Generic.List<ChatMessage> { new SystemChatMessage(systemPrompt) };

        // Banner
        Console.OutputEncoding = Encoding.UTF8;
        WriteInfo("──────────────────────────────────────────────────────────────");
        WriteInfo(" Azure AI 101 — .NET Multi-Tool Agent (KPI + Weather REST) ");
        WriteInfo(" Commands: /exit, /reset, /sys <text>, /temp <0..2>, /unit c|f ");
        WriteInfo("──────────────────────────────────────────────────────────────\n");

        // REPL
        while (true)
        {
            WriteRole("you> ", ConsoleColor.Cyan);
            var input = Console.ReadLine();
            if (input is null) break;
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.StartsWith("/"))
            {
                if (HandleCommand(input)) continue;
            }

            var userMessage = input;
            if (!input.Contains(" in ", StringComparison.OrdinalIgnoreCase)
                && input.Contains("temperature", StringComparison.OrdinalIgnoreCase))
            {
                userMessage += $" (use unit '{defaultTempUnit}')";
            }

            history.Add(new UserChatMessage(userMessage));

            var opts = new ChatCompletionOptions
            {
                Temperature = (float)modelTemperature,
                MaxOutputTokenCount = 800
            };
            opts.Tools.Add(lookupKpiTool);
            opts.Tools.Add(getWeatherTool);

            using var spin = new Spinner("thinking…");
            var swTotal = Stopwatch.StartNew();
            var (final, calls) = await RunAgentAsync(history, opts, spin);
            swTotal.Stop();
            spin.Done($"done in {swTotal.ElapsedMilliseconds} ms (round trips: {calls})");

            WriteRole("assistant> ", ConsoleColor.Green);
            WriteWrapped(final, ConsoleColor.Green);
            Console.WriteLine();
        }
    }

    // ===== Client factory (Project or Resource) =====
    static async Task<ChatClient> CreateChatClientAsync(string? projectEndpoint, string? resourceEndpoint, string deployment)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<Program>(optional: true)
            .Build();
        // Prefer explicit API key if set (fast and unambiguous)
        var apiKey = config["AZURE_OPENAI_KEY"];

        if (!string.IsNullOrWhiteSpace(projectEndpoint))
        {
            // Project mode:
            // Project URL → https://<res>.services.ai.azure.com/api/projects/<proj>
            // Inference base → https://<res>.services.ai.azure.com/openai/v1/
            var u = new Uri(projectEndpoint, UriKind.Absolute);
            var baseUrl = $"{u.Scheme}://{u.Host}/openai/v1/";
            WriteInfo($"project mode: {projectEndpoint}");
            WriteInfo($"inference base: {baseUrl}");
            WriteInfo("auth: api-key");
            return new ChatClient(
                model: deployment,
                credential: new ApiKeyCredential(apiKey),
                options: new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
           
        }
        else if (!string.IsNullOrWhiteSpace(resourceEndpoint))
        {
            // Classic Azure OpenAI resource mode
            var baseUrl = new Uri(new Uri(resourceEndpoint), "/openai/v1/").ToString();
            WriteInfo($"resource mode: {resourceEndpoint}");
            WriteInfo($"inference base: {baseUrl}");

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                WriteInfo("auth: api-key");
                return new ChatClient(
                    model: deployment,
                    credential: new ApiKeyCredential(apiKey),
                    options: new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
            }
            else
            {
                // Or use AzureOpenAIClient helper + Entra ID
                var aoai = new AzureOpenAIClient(new Uri(resourceEndpoint), new DefaultAzureCredential());
                // probe via aoai to catch RBAC issues fast
                await ProbeAsync(aoai.GetChatClient(deployment));
                return aoai.GetChatClient(deployment);
            }
        }
        else
        {
            throw new InvalidOperationException("Provide either 'projectEndpoint' or 'endpoint' in appsettings.json.");
        }
    }

    static async Task ProbeAsync(ChatClient client)
    {
        var msgs = new System.Collections.Generic.List<ChatMessage> {
            new SystemChatMessage("probe"), new UserChatMessage("ping")
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.CompleteChatAsync(msgs, new ChatCompletionOptions { Temperature = 0 }, cts.Token);
    }

    // ===== Commands =====
    static bool HandleCommand(string cmd)
    {
        var parts = cmd.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit":
                Environment.Exit(0); return true;
            case "/reset":
                history.Clear(); history.Add(new SystemChatMessage(systemPrompt));
                WriteWarn("conversation reset (system prompt retained)."); return true;
            case "/sys":
                if (parts.Length == 2)
                {
                    systemPrompt = parts[1].Trim();
                    history.Clear(); history.Add(new SystemChatMessage(systemPrompt));
                    WriteWarn("system prompt updated & conversation reset.");
                }
                else WriteError("usage: /sys <new system prompt>");
                return true;
            case "/temp":
                if (parts.Length == 2 && double.TryParse(parts[1], out var t) && t >= 0 && t <= 2)
                { modelTemperature = t; WriteWarn($"temperature set to {modelTemperature:0.00}"); }
                else WriteError("usage: /temp <0..2>");
                return true;
            case "/unit":
                if (parts.Length == 2 && (parts[1].Equals("c", StringComparison.OrdinalIgnoreCase) ||
                                          parts[1].Equals("f", StringComparison.OrdinalIgnoreCase)))
                { defaultTempUnit = parts[1].ToLowerInvariant(); WriteWarn($"default weather unit set to '{defaultTempUnit}'"); }
                else WriteError("usage: /unit c|f");
                return true;
            default:
                WriteError("unknown command. try: /exit, /reset, /sys, /temp, /unit"); return true;
        }
    }

    // ===== Agent loop with timeouts & tools =====
    static async Task<(string final, int calls)> RunAgentAsync(
        System.Collections.Generic.List<ChatMessage> convo,
        ChatCompletionOptions options,
        Spinner spinner,
        int maxToolRounds = 4)
    {
        int rounds = 0;

        while (true)
        {
            spinner.Update("thinking…");
            ChatCompletion completion;
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                completion = await chat.CompleteChatAsync(convo, options, cts.Token);
            }
            catch (RequestFailedException ex)
            {
                WriteError($"Azure OpenAI error: {ex.Status} {ex.ErrorCode} {ex.Message}");
                return ("(call failed – see error above)", rounds + 1);
            }
            catch (OperationCanceledException)
            {
                WriteError("Timed out waiting for response (check network/auth or increase timeout).");
                return ("(timed out)", rounds + 1);
            }
            finally
            {
                sw.Stop();
                WriteInfo($"> model_call[{rounds}] {sw.ElapsedMilliseconds} ms");
            }

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                // capture the assistant message with tool calls
                convo.Add(new AssistantChatMessage(completion));

                foreach (ChatToolCall call in completion.ToolCalls)
                {
                    string output;
                    try
                    {
                        switch (call.FunctionName)
                        {
                            case "lookup_kpi":
                                {
                                    using var args = JsonDocument.Parse(call.FunctionArguments.ToString());
                                    string metric = args.RootElement.TryGetProperty("metric", out var m)
                                                    ? m.GetString() ?? "MRR" : "MRR";
                                    spinner.Update($"calling lookup_kpi(\"{metric}\")…");
                                    output = LookupKpi(metric);
                                    break;
                                }
                            case "get_weather":
                                {
                                    using var args = JsonDocument.Parse(call.FunctionArguments.ToString());
                                    if (!args.RootElement.TryGetProperty("location", out var locEl))
                                    {
                                        output = "{\"error\":\"missing_location\"}";
                                        break;
                                    }
                                    string location = locEl.GetString() ?? "";
                                    string unit = args.RootElement.TryGetProperty("unit", out var uEl)
                                                  ? (uEl.GetString() ?? "c") : "c";

                                    spinner.Update($"calling get_weather(\"{location}\", \"{unit}\")…");
                                    var (tempC, resolved) = await weatherClient.GetCurrentTempAsync(location);
                                    double value = unit.Equals("f", StringComparison.OrdinalIgnoreCase)
                                        ? (tempC * 9.0 / 5.0 + 32.0) : tempC;

                                    output = JsonSerializer.Serialize(new
                                    {
                                        location = resolved,
                                        temperature = Math.Round(value, 1),
                                        unit = unit.Equals("f", StringComparison.OrdinalIgnoreCase) ? "°F" : "°C",
                                        source = "open-meteo"
                                    });
                                    break;
                                }
                            default:
                                output = JsonSerializer.Serialize(new { error = "unknown_tool", name = call.FunctionName });
                                break;
                        }
                    }
                    catch (JsonException)
                    {
                        output = "{\"error\":\"bad_arguments\"}";
                    }
                    catch (Exception ex)
                    {
                        output = JsonSerializer.Serialize(new { error = "tool_exception", message = ex.Message });
                    }

                    // feed the tool result back
                    convo.Add(new ToolChatMessage(call.Id, output));
                }

                rounds++;
                if (rounds >= maxToolRounds)
                    return ("(stopped after too many tool rounds)", rounds);

                continue; // next turn with tool results appended
            }

            // final assistant reply
            var text = completion.Content.Count > 0 ? completion.Content[0].Text : "(no text)";
            convo.Add(new AssistantChatMessage(completion)); // keep history
            return (text, rounds + 1);
        }
    }

    // ===== Tool implementations =====
    static string LookupKpi(string metric)
    {
        string m = (metric ?? "").Trim().ToUpperInvariant();
        return m switch
        {
            "MRR" => "MRR (Monthly Recurring Revenue): Sum of normalized monthly subscription revenue. Ex: sum(plan_price * seats).",
            "NPS" => "NPS (Net Promoter Score): %Promoters - %Detractors from survey responses.",
            _ => "Unknown metric. Try MRR or NPS."
        };
    }

    // Weather REST client (Open-Meteo, no key)
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
