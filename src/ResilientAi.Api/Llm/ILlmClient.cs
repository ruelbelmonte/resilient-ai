namespace ResilientAi.Api.Llm;

/// <summary>What the model returned, including token usage for cost metrics.</summary>
public sealed record LlmResponse(string Text, string Model, int InputTokens, int OutputTokens);

/// <summary>
/// Raised for failures worth retrying (5xx including 529, 429, network errors).
/// <see cref="RetryAfter"/> carries the provider's Retry-After hint when it sent one.
/// </summary>
public sealed class TransientLlmException : Exception
{
    public TransientLlmException(string message, Exception? inner = null)
        : base(message, inner) { }

    public TransientLlmException(string message, TimeSpan? retryAfter)
        : base(message) => RetryAfter = retryAfter;

    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Raised when the provider rejects the request (4xx other than 429) or returns a
/// body that cannot be parsed. Retrying would not change the answer.
/// </summary>
public sealed class PermanentLlmException : Exception
{
    public PermanentLlmException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>Raised when the client is missing configuration it needs, such as the API key.</summary>
public sealed class LlmNotConfiguredException : Exception
{
    public LlmNotConfiguredException(string message)
        : base(message) { }
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
