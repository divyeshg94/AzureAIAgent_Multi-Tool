using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AzureAIAgent.Security;

/// <summary>
/// Configures OpenTelemetry tracing for the agent.
///
/// Blog reference: "Every tool call is traced with OpenTelemetry and
/// shipped to Azure Monitor."
///
/// Every tool invocation should be wrapped in a span so you can answer:
/// - Which tool was called?
/// - What was the input?
/// - What did it return?
/// - How long did it take?
/// </summary>
public static class AgentTelemetry
{
    public static readonly ActivitySource Source =
        new("AzureAIAgent.MultiTool", "1.0.0");

    public static TracerProvider Build()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService("AzureAIAgent.MultiTool"))
            .AddSource(Source.Name);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.AddAzureMonitorTraceExporter(o =>
                o.ConnectionString = connectionString);
        }
        else
        {
            // Fall back to console output in local development
            builder.AddConsoleExporter();
        }

        return builder.Build();
    }

    /// <summary>
    /// Wraps a tool call in a named span. Use this in every tool handler.
    ///
    /// Usage:
    ///   using var span = AgentTelemetry.StartToolCall("WeatherTool", userInput);
    ///   var result = await CallWeatherApi(userInput);
    ///   span?.SetTag("tool.result.length", result.Length);
    /// </summary>
    public static Activity? StartToolCall(string toolName, string input)
    {
        var activity = Source.StartActivity(
            $"tool.{toolName}",
            ActivityKind.Internal);

        activity?.SetTag("tool.name", toolName);
        activity?.SetTag("tool.input.length", input.Length);
        activity?.SetTag("tool.input.preview",
            input.Length > 100 ? input[..100] + "..." : input);

        return activity;
    }
}
