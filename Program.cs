using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using AzureAIAgent.Security;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using OpenAI.Images;      // ChatClient, ChatMessage, ChatTool, etc.

namespace AzureAIAgent101;

internal class Program
{

    // ===== SDK objects & state =====
    static ChatClient chat = null!;
    static Tools.OpenMeteoWeatherClient weatherClient = null!;
    static double modelTemperature = 0.2;
    static string defaultTempUnit = "c";
    static string systemPrompt =
        "You are a precise .NET research assistant. " +
        "Use tools when helpful. Use get_weather for any weather/temperature question. " +
        "If you call lookup_kpi, cite 'local-kb'. " +
        "Return: TL;DR (1 line), bullet points, and 'Sources' at the end.";
    static System.Collections.Generic.List<ChatMessage> history = new();

    // Added: hold inference base for HTTP calls
    static string inferenceBase = string.Empty; // ends with /openai/v1/
    static string projectEndpoint = string.Empty;
    static string speechModel = string.Empty;   // voice model/deployment for speech endpoint
    static string videoModel = string.Empty;    // video model/deployment
    static string videoInferenceBase = string.Empty; // separate base for video if provided
    static ToolContext? toolContext;              // holds state for tool handlers

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

    static ChatTool generateImageTool = ChatTool.CreateFunctionTool(
        functionName: "generate_image",
        functionDescription: "Generate an image based on a textual description.",
        functionParameters: BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "prompt": { "type": "string", "description": "Image description" }
          },
            "required": ["prompt"]
        }
        """
        )
    );

    static ChatTool speakTool = ChatTool.CreateFunctionTool(
        functionName: "speak_summary",
        functionDescription: "Convert a text summary into spoken audio.",
        functionParameters: BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "Text to narrate" }
          },
          "required": ["text"]
        }
        """
        )
    );

    static ChatTool videoTool = ChatTool.CreateFunctionTool(
        functionName: "generate_video",
        functionDescription: "Generate a short video clip (mp4) from a text prompt and provide a local HTML playground to view it.",
        functionParameters: BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "prompt": { "type": "string", "description": "Video description" },
            "durationSeconds": { "type": "integer", "description": "Requested duration (<=10)", "minimum": 1, "maximum": 10 }
          },
          "required": ["prompt"]
        }
        """
        )
    );

    public static async Task Main()
    {
        // Initialize telemetry
        using var tracerProvider = AgentTelemetry.Build();

        // ===== Config =====
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<Program>(optional: true)
            .Build();

        projectEndpoint = config["projectEndpoint"] ?? string.Empty;   // e.g., https://...services.ai.azure.com/api/projects/firstProject
        var resourceEndpoint = config["endpoint"] ?? string.Empty;         // e.g., https://<resource>.openai.azure.com/
        var deployment = config["model"];                  // DEPLOYMENT name (chat)
        speechModel = config["Speech_Model"] ?? config["speechModel"] ?? config["Audio_Model"] ?? string.Empty; // attempt multiple keys
        videoModel = config["Video_Model"] ?? config["videoModel"] ?? string.Empty;
        videoInferenceBase = config["videoEndpoint"] ?? config["videoEndpoint"] ?? resourceEndpoint ?? string.Empty;
        if (string.IsNullOrWhiteSpace(deployment))
        {
            ConsoleUI.WriteError("Missing 'model' (deployment name) in config.");
            return;
        }
        if (string.IsNullOrWhiteSpace(speechModel))
        {
            ConsoleUI.WriteWarn("No speech model configured (Speech_Model). speak_summary tool will be disabled.");
        }
        if (string.IsNullOrWhiteSpace(videoModel))
        {
            ConsoleUI.WriteWarn("No video model configured (Video_Model). generate_video tool will be disabled.");
        }

        // Build ChatClient for either Project or classic Resource mode
        chat = await CreateChatClientAsync(projectEndpoint, resourceEndpoint, deployment);

        // Other clients
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        weatherClient = new Tools.OpenMeteoWeatherClient(http);

        // Create tool context for handlers
        toolContext = new ToolContext(
            SpeechModel: speechModel,
            VideoModel: videoModel,
            InferenceBase: inferenceBase,
            ProjectEndpoint: projectEndpoint,
            WeatherClient: weatherClient
        );

        history = new System.Collections.Generic.List<ChatMessage> { new SystemChatMessage(systemPrompt) };

        // Banner
        Console.OutputEncoding = Encoding.UTF8;
        ConsoleUI.WriteInfo("──────────────────────────────────────────────────────────────");
        ConsoleUI.WriteInfo(" Azure AI 101 — .NET Multi-Tool Agent (KPI + Weather REST) ");
        ConsoleUI.WriteInfo(" Commands: /exit, /reset, /sys <text>, /temp <0..2>, /unit c|f ");
        ConsoleUI.WriteInfo("──────────────────────────────────────────────────────────────\n");

        // REPL
        while (true)
        {
            ConsoleUI.WriteRole("you> ", ConsoleColor.Cyan);
            var input = Console.ReadLine();
            if (input is null) break;
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.StartsWith("/"))
            {
                if (HandleCommand(input)) continue;
            }

            // Validate input before proceeding to the agent
            var validation = InputGuard.Validate(input);
            if (!validation.IsValid)
            {
                ConsoleUI.WriteError($"[Rejected] {validation.RejectionReason}");
                continue;
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
            opts.Tools.Add(generateImageTool);
            if (!string.IsNullOrWhiteSpace(speechModel))
                opts.Tools.Add(speakTool); // only if configured
            if (!string.IsNullOrWhiteSpace(videoModel))
                opts.Tools.Add(videoTool); // only if configured

            using var spin = new Spinner("thinking…");
            var swTotal = Stopwatch.StartNew();
            var (final, calls) = await RunAgentAsync(history, opts, spin);
            swTotal.Stop();
            spin.Done($"done in {swTotal.ElapsedMilliseconds} ms (round trips: {calls})");

            ConsoleUI.WriteRole("assistant> ", ConsoleColor.Green);
            ConsoleUI.WriteWrapped(final, ConsoleColor.Green);
            Console.WriteLine();
        }
    }

    // ===== Client factory (Project or Resource) =====
    static async Task<ChatClient> CreateChatClientAsync(string? projectEndpoint, string? resourceEndpoint, string deployment)
    {
        var credential = IdentitySetup.CreateCredential();

        if (!string.IsNullOrWhiteSpace(projectEndpoint))
        {
            ConsoleUI.WriteInfo($"project mode: {projectEndpoint}");
            ConsoleUI.WriteInfo("auth: DefaultAzureCredential (Managed Identity)");
            
            var u = new Uri(projectEndpoint, UriKind.Absolute);
            inferenceBase = $"{u.Scheme}://{u.Host}/openai/v1/";
            ConsoleUI.WriteInfo($"inference base: {inferenceBase}");
            
            var aoai = new AzureOpenAIClient(u, credential);
            return aoai.GetChatClient(deployment);
        }
        else if (!string.IsNullOrWhiteSpace(resourceEndpoint))
        {
            ConsoleUI.WriteInfo($"resource mode: {resourceEndpoint}");
            ConsoleUI.WriteInfo("auth: DefaultAzureCredential (Managed Identity)");
            
            inferenceBase = new Uri(new Uri(resourceEndpoint), "/openai/v1/").ToString();
            ConsoleUI.WriteInfo($"inference base: {inferenceBase}");

            var aoai = new AzureOpenAIClient(new Uri(resourceEndpoint), credential);
            await ProbeAsync(aoai.GetChatClient(deployment));
            return aoai.GetChatClient(deployment);
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
                ConsoleUI.WriteWarn("conversation reset (system prompt retained)."); return true;
            case "/sys":
                if (parts.Length == 2)
                {
                    systemPrompt = parts[1].Trim();
                    history.Clear(); history.Add(new SystemChatMessage(systemPrompt));
                    ConsoleUI.WriteWarn("system prompt updated & conversation reset.");
                }
                else ConsoleUI.WriteError("usage: /sys <new system prompt>");
                return true;
            case "/temp":
                if (parts.Length == 2 && double.TryParse(parts[1], out var t) && t >= 0 && t <= 2)
                { modelTemperature = t; ConsoleUI.WriteWarn($"temperature set to {modelTemperature:0.00}"); }
                else ConsoleUI.WriteError("usage: /temp <0..2>");
                return true;
            case "/unit":
                if (parts.Length == 2 && (parts[1].Equals("c", StringComparison.OrdinalIgnoreCase) ||
                                          parts[1].Equals("f", StringComparison.OrdinalIgnoreCase)))
                { defaultTempUnit = parts[1].ToLowerInvariant(); ConsoleUI.WriteWarn($"default weather unit set to '{defaultTempUnit}'"); }
                else ConsoleUI.WriteError("usage: /unit c|f");
                return true;
            default:
                ConsoleUI.WriteError("unknown command. try: /exit, /reset, /sys, /temp, /unit"); return true;
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
                ConsoleUI.WriteError($"Azure OpenAI error: {ex.Status} {ex.ErrorCode} {ex.Message}");
                return ("(call failed – see error above)", rounds + 1);
            }
            catch (OperationCanceledException)
            {
                ConsoleUI.WriteError("Timed out waiting for response (check network/auth or increase timeout).");
                return ("(timed out)", rounds + 1);
            }
            finally
            {
                sw.Stop();
                ConsoleUI.WriteInfo($"> model_call[{rounds}] {sw.ElapsedMilliseconds} ms");
            }

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                convo.Add(new AssistantChatMessage(completion));

                foreach (ChatToolCall call in completion.ToolCalls)
                {
                    string output;
                    try
                    {
                        switch (call.FunctionName)
                        {
                            case "lookup_kpi":
                                output = await ToolHandlers.HandleKpiToolAsync(call, spinner, toolContext!);
                                break;
                            case "get_weather":
                                output = await ToolHandlers.HandleWeatherToolAsync(call, spinner, toolContext!);
                                break;
                            case "generate_image":
                                output = await ToolHandlers.HandleImageToolAsync(call, spinner, toolContext!);
                                break;
                            case "speak_summary":
                                output = await ToolHandlers.HandleSpeechToolAsync(call, spinner, toolContext!);
                                break;
                            case "generate_video":
                                output = await ToolHandlers.HandleVideoToolAsync(call, spinner, toolContext!);
                                break;
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

                    convo.Add(new ToolChatMessage(call.Id, output));
                }

                rounds++;
                if (rounds >= maxToolRounds)
                    return ("(stopped after too many tool rounds)", rounds);

                continue;
            }

            var textOut = completion.Content.Count > 0 ? completion.Content[0].Text : "(no text)";
            convo.Add(new AssistantChatMessage(completion));
            return (textOut, rounds + 1);
        }
    }

}

