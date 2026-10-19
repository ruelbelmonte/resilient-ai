using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ResilientAi.Api.Llm;
using Xunit;

namespace ResilientAi.Tests;

/// <summary>
/// Pins how the real client maps provider responses onto the exception types the
/// pipeline and service act on, using a stub handler instead of the network.
/// </summary>
public class AnthropicLlmClientTests
{
    private const string ValidBody = """
        {"model":"claude-sonnet-5-5","content":[{"type":"text","text":"a summary"}],
         "usage":{"input_tokens":12,"output_tokens":3}}
        """;

    [Fact]
    public async Task Parses_text_usage_and_model_and_sends_the_configured_model()
    {
        var stub = new StubHandler(HttpStatusCode.OK, ValidBody);
        var response = await Client(stub).CompleteAsync("prompt", CancellationToken.None);

        Assert.Equal(new LlmResponse("a summary", "claude-sonnet-5-5", 12, 3), response);
        Assert.Equal("test-key", stub.Request!.Headers.GetValues("x-api-key").Single());
        using var sent = JsonDocument.Parse(stub.RequestBody!);
        Assert.Equal("claude-sonnet-5-5", sent.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Rate_limit_is_transient_and_carries_retry_after()
    {
        var stub = new StubHandler((HttpStatusCode)429, "{}", retryAfterSeconds: 7);
        var ex = await Assert.ThrowsAsync<TransientLlmException>(
            () => Client(stub).CompleteAsync("prompt", CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(529)]
    public async Task Server_errors_and_overload_are_transient(int status)
    {
        var stub = new StubHandler((HttpStatusCode)status, "{}");
        var ex = await Assert.ThrowsAsync<TransientLlmException>(
            () => Client(stub).CompleteAsync("prompt", CancellationToken.None));

        Assert.Null(ex.RetryAfter);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task Client_errors_are_permanent(int status)
    {
        var stub = new StubHandler((HttpStatusCode)status, """{"error":{"message":"echoes input"}}""");
        var ex = await Assert.ThrowsAsync<PermanentLlmException>(
            () => Client(stub).CompleteAsync("prompt", CancellationToken.None));

        Assert.DoesNotContain("echoes input", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"content":"oops","usage":{}}""")]
    public async Task Malformed_body_is_permanent(string body)
    {
        var stub = new StubHandler(HttpStatusCode.OK, body);
        await Assert.ThrowsAsync<PermanentLlmException>(
            () => Client(stub).CompleteAsync("prompt", CancellationToken.None));
    }

    [Fact]
    public async Task Missing_api_key_fails_without_calling_the_provider()
    {
        var stub = new StubHandler(HttpStatusCode.OK, ValidBody);
        await Assert.ThrowsAsync<LlmNotConfiguredException>(
            () => Client(stub, apiKey: null).CompleteAsync("prompt", CancellationToken.None));

        Assert.Null(stub.Request);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(503)]
    public async Task Disposes_the_response_on_every_path(int status)
    {
        var stub = new StubHandler((HttpStatusCode)status, ValidBody);
        try
        {
            await Client(stub).CompleteAsync("prompt", CancellationToken.None);
        }
        catch (Exception ex) when (ex is TransientLlmException or PermanentLlmException)
        {
        }

        Assert.True(stub.Content!.Disposed);
    }

    private static AnthropicLlmClient Client(StubHandler handler, string? apiKey = "test-key") =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.test/") },
            Options.Create(new LlmOptions { ApiKey = apiKey, Model = "claude-sonnet-5-5" }));
}

public sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;
    private readonly int? _retryAfterSeconds;

    public StubHandler(HttpStatusCode status, string body, int? retryAfterSeconds = null)
    {
        _status = status;
        _body = body;
        _retryAfterSeconds = retryAfterSeconds;
    }

    public HttpRequestMessage? Request { get; private set; }
    public string? RequestBody { get; private set; }
    public TrackingContent? Content { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        Content = new TrackingContent(_body);
        var response = new HttpResponseMessage(_status) { Content = Content };
        if (_retryAfterSeconds is { } seconds)
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
        return response;
    }
}

public sealed class TrackingContent : StringContent
{
    public TrackingContent(string body)
        : base(body, Encoding.UTF8, "application/json") { }

    public bool Disposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
