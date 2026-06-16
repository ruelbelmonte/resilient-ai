namespace ResilientAi.Api;

/// <summary>
/// How a request failed, so the transport can map it to the right status code
/// without the service knowing about HTTP. See DESIGN.md: the Result pattern is
/// used for expected outcomes so they do not travel as exceptions.
/// </summary>
public enum ErrorKind
{
    None,
    Validation,
    Unavailable,
}

/// <summary>A success carrying a value, or a failure carrying a reason.</summary>
public sealed class Result<T>
{
    private Result(bool isSuccess, T? value, string? error, ErrorKind errorKind)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        ErrorKind = errorKind;
    }

    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public ErrorKind ErrorKind { get; }

    public static Result<T> Success(T value) => new(true, value, null, ErrorKind.None);

    public static Result<T> Failure(ErrorKind kind, string error) =>
        new(false, default, error, kind);
}
