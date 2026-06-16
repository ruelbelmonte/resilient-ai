using System.Net;
using System.Text;
using System.Text.Json;

namespace ResilientAi.Api.Llm;

/// <summary>
/// Real model client over the Anthropic Messages API using a typed HttpClient.
/// Maps 5xx, 429, and network timeouts to <see cref="TransientLlmException"/> so
/// the resilience pipeline can retry them; everything else fails fast. Not
/// exercised by the test suite, which injects a fake instead.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private const int MaxTokens = 1024;

    public AnthropicLlmClient(HttpClient http, IConfiguration config)
    {
        _http = http;
        _apiKey = config["ANTHROPIC_API_KEY"] ?? string.Empty;
        _model = config["LLM_MODEL"] ?? "claude-sonnet-4-6";
    }

    public async Task<LlmResponse> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_apiKey))
            throw new InvalidOperationException("ANTHROPIC_API_KEY is not configured");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        var payload = new
        {
            model = _model,
            max_tokens = MaxTokens,
            messages = new[] { new { role = "user", content = prompt } },
        };
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TransientLlmException("network error calling the model", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientLlmException("timeout calling the model", ex);
        }

        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new TransientLlmException($"model returned {(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        var text = new StringBuilder();
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            if (block.GetProperty("type").GetString() == "text")
                text.Append(block.GetProperty("text").GetString());
        }

        var usage = root.GetProperty("usage");
        return new LlmResponse(
            text.ToString(),
            usage.GetProperty("input_tokens").GetInt32(),
            usage.GetProperty("output_tokens").GetInt32());
    }
}
