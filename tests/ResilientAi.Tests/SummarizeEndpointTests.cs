using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResilientAi.Api.Llm;
using Xunit;

namespace ResilientAi.Tests;

/// <summary>
/// Drives the real app end to end through WebApplicationFactory, swapping only
/// the model for a fake. Pins the transport contract: status-code mapping from
/// the Result, validation, and the health endpoint.
/// </summary>
public class SummarizeEndpointTests
{
    private static WebApplicationFactory<Program> Factory(ILlmClient llm) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILlmClient>();
                services.AddSingleton(llm);
            }));

    [Fact]
    public async Task Valid_text_returns_200_with_a_summary()
    {
        using var factory = Factory(new FakeLlmClient());
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/summarize", new { text = "some text to summarise" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SummarizeResponse>();
        Assert.False(string.IsNullOrEmpty(body!.Summary));
    }

    [Fact]
    public async Task Empty_text_returns_400()
    {
        using var factory = Factory(new FakeLlmClient());
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/summarize", new { text = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Model_failure_returns_503()
    {
        using var factory = Factory(new FakeLlmClient(alwaysFail: true));
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/summarize", new { text = "some text" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_returns_200()
    {
        using var factory = Factory(new FakeLlmClient());
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
