using ResilientAi.Api;
using Xunit;

namespace ResilientAi.Tests;

public class ResultTests
{
    [Fact]
    public void Success_carries_the_value()
    {
        var result = Result<int>.Success(42);
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(ErrorKind.None, result.ErrorKind);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_carries_the_kind_and_message()
    {
        var result = Result<int>.Failure(ErrorKind.Validation, "bad input");
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.ErrorKind);
        Assert.Equal("bad input", result.Error);
    }
}
