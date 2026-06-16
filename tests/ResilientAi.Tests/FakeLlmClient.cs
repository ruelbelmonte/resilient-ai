using ResilientAi.Api.Llm;

namespace ResilientAi.Tests;

/// <summary>
/// Deterministic stand-in for the model. Can succeed, fail a set number of times
/// before succeeding (to exercise retries), or always fail (to exercise the
/// exhausted-pipeline path). Records how many times it was called.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly int _failBeforeSuccess;
    private readonly bool _alwaysFail;

    public FakeLlmClient(int failBeforeSuccess = 0, bool alwaysFail = false)
    {
        _failBeforeSuccess = failBeforeSuccess;
        _alwaysFail = alwaysFail;
    }

    public int Calls { get; private set; }

    public Task<LlmResponse> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        Calls++;
        if (_alwaysFail || Calls <= _failBeforeSuccess)
            throw new TransientLlmException("simulated transient failure");

        return Task.FromResult(new LlmResponse($"summary ({prompt.Length} chars)", 100, 20));
    }
}
