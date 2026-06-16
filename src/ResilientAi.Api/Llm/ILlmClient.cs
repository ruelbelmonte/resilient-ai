namespace ResilientAi.Api.Llm;

/// <summary>What the model returned, including token usage for cost metrics.</summary>
public sealed record LlmResponse(string Text, int InputTokens, int OutputTokens);

/// <summary>
/// Raised for failures worth retrying (5xx, 429, network timeouts). The
/// resilience pipeline retries on this type; anything else fails fast.
/// </summary>
public sealed class TransientLlmException : Exception
{
    public TransientLlmException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// The only thing the service needs from a model provider. Implemented for real
/// by <c>AnthropicLlmClient</c> and by a fake in the test suite, so the service
/// is tested without a network or an API key.
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(string prompt, CancellationToken cancellationToken);
}
