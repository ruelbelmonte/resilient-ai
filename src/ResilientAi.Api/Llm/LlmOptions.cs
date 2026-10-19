namespace ResilientAi.Api.Llm;

/// <summary>Bound from the <c>Llm</c> configuration section.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Model { get; set; } = "claude-sonnet-5-5";

    /// <summary>Read from <c>ANTHROPIC_API_KEY</c>, never from appsettings.</summary>
    public string? ApiKey { get; set; }

    public LlmResilienceOptions Resilience { get; set; } = new();

    /// <summary>List prices keyed by model id. A model with no entry records no cost.</summary>
    public Dictionary<string, ModelPrice> Pricing { get; set; } = new();
}

public sealed class LlmResilienceOptions
{
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetryAttempts { get; set; } = 2;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class ModelPrice
{
    public double InputUsdPerMTok { get; set; }
    public double OutputUsdPerMTok { get; set; }
}
