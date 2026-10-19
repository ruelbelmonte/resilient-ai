using ResilientAi.Api.Llm;

namespace ResilientAi.Tests;

/// <summary>
/// Deterministic stand-in for the model. Can hang (until cancelled) or fail a set
/// number of times before succeeding, or do so on every call. Records how many
/// times it was called.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly int _failBeforeSuccess;
    private readonly bool _alwaysFail;
    private readonly int _hangBeforeSuccess;
    private readonly bool _alwaysHang;
    private readonly Func<Exception> _failure;
    private readonly string _model;

    public FakeLlmClient(
        int failBeforeSuccess = 0,
        bool alwaysFail = false,
        int hangBeforeSuccess = 0,
        bool alwaysHang = false,
        Func<Exception>? failWith = null,
        string model = "claude-sonnet-5-5")
    {
        _failBeforeSuccess = failBeforeSuccess;
        _alwaysFail = alwaysFail;
        _hangBeforeSuccess = hangBeforeSuccess;
        _alwaysHang = alwaysHang;
        _failure = failWith ?? (() => new TransientLlmException("simulated transient failure"));
        _model = model;
    }

    public int Calls { get; private set; }

    public async Task<LlmResponse> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        Calls++;
        if (_alwaysHang || Calls <= _hangBeforeSuccess)
            await Task.Delay(Timeout.Infinite, cancellationToken);
        if (_alwaysFail || Calls <= _failBeforeSuccess)
            throw _failure();

        return new LlmResponse($"summary ({prompt.Length} chars)", _model, 100, 20);
    }
}
