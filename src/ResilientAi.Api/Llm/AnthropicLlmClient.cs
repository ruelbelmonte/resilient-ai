using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ResilientAi.Api.Llm;

/// <summary>
/// Real model client over the Anthropic Messages API using a typed HttpClient.
/// Maps 5xx (including 529 overloaded), 429, and network errors to
/// <see cref="TransientLlmException"/> so the pipeline can retry them; other 4xx
/// and unparseable bodies become <see cref="PermanentLlmException"/>. Timeouts are
/// owned by the resilience pipeline, not by this class or HttpClient.Timeout.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    private const int MaxTokens = 1024;

    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public AnthropicLlmClient(HttpClient http, IOptions<LlmOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<LlmResponse> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
            throw new LlmNotConfiguredException("ANTHROPIC_API_KEY is not configured");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        var payload = new
        {
            model = _options.Model,
            max_tokens = MaxTokens,
            messages = new[] { new { role = "user", content = prompt } },
        };
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await SendAsync(request, cancellationToken);

        var status = (int)response.StatusCode;
        if (status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new TransientLlmException($"model returned {status}", RetryAfter(response));
        // The error body is not logged or surfaced: it can echo the caller's input.
        if (!response.IsSuccessStatusCode)
            throw new PermanentLlmException($"model returned {status}");

        return await ParseAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TransientLlmException("network error calling the model", ex);
        }
    }

    private async Task<LlmResponse> ParseAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var text = new StringBuilder();
            foreach (var block in root.GetProperty("content").EnumerateArray())
            {
                if (block.GetProperty("type").GetString() == "text")
                    text.Append(block.GetProperty("text").GetString());
            }

            var model = root.TryGetProperty("model", out var m) && m.GetString() is { } id
                ? id
                : _options.Model;
            var usage = root.GetProperty("usage");
            return new LlmResponse(
                text.ToString(),
                model,
                usage.GetProperty("input_tokens").GetInt32(),
                usage.GetProperty("output_tokens").GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new PermanentLlmException("model returned a malformed response", ex);
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } when date > DateTimeOffset.UtcNow => date - DateTimeOffset.UtcNow,
            { Date: not null } => TimeSpan.Zero,
            _ => null,
        };
}
