namespace AzureAIAgent.Security;

/// <summary>
/// Validates user input before it reaches the agent or any tool.
///
/// Blog reference: "build a classification layer that routes or rejects
/// inputs outside the agent's intended domain before they ever reach
/// the model."
///
/// Extend AllowedTopics as the agent's scope grows.
/// </summary>
public static class InputGuard
{
    private static readonly string[] AllowedTopics =
    [
        "weather", "temperature", "forecast", "climate",
        "kpi", "metric", "mrr", "nps", "revenue", "churn",
        "image", "generate", "create", "visualise", "visualize",
        "audio", "narrate", "speech", "voice",
        "video", "clip", "animate",
        "compare", "define", "explain", "summarise", "summarize",
        "what", "how", "show", "tell", "give", "check"
    ];

    private static readonly string[] BlockedPatterns =
    [
        "ignore previous instructions",
        "ignore all instructions",
        "you are now",
        "system prompt",
        "forget your instructions",
        "act as",
        "jailbreak",
        "disregard",
        "override"
    ];

    /// <summary>
    /// Returns true if the input is within the agent's domain and
    /// shows no obvious prompt injection patterns.
    /// </summary>
    public static ValidationResult Validate(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ValidationResult.Reject("Input cannot be empty.");

        if (input.Length > 500)
            return ValidationResult.Reject(
                "Input is too long. Please keep queries under 500 characters.");

        var lower = input.ToLowerInvariant();

        foreach (var pattern in BlockedPatterns)
        {
            if (lower.Contains(pattern))
                return ValidationResult.Reject(
                    $"Input contains a disallowed pattern: '{pattern}'. " +
                    "Please ask about weather, KPIs, or media generation.");
        }

        var hasKnownTopic = AllowedTopics.Any(topic => lower.Contains(topic));
        if (!hasKnownTopic)
            return ValidationResult.Reject(
                "That topic is outside this agent's scope. " +
                "Try asking about weather, KPIs, or generating media.");

        return ValidationResult.Accept();
    }

    /// <summary>
    /// Wraps retrieved document content in explicit delimiters so the
    /// model can distinguish trusted instructions from untrusted data.
    ///
    /// Blog reference: "Untrusted content... should be clearly delimited
    /// in the prompt."
    /// </summary>
    public static string WrapRetrievedContent(string content, string source)
    {
        return $"""
            --- BEGIN RETRIEVED CONTENT FROM {source} ---
            {content}
            --- END RETRIEVED CONTENT ---
            """;
    }
}

public record ValidationResult(bool IsValid, string? RejectionReason)
{
    public static ValidationResult Accept() => new(true, null);
    public static ValidationResult Reject(string reason) => new(false, reason);
}
