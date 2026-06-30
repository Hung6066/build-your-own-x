using FluentAssertions;
using Hope.Agent.Shared;
using Xunit;

namespace Hope.Agent.Tests.Unit.Shared;

public sealed class ResultTests
{
    [Fact]
    public void Success_carries_value_and_no_error()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(42);
        result.Error.Should().Be(Error.None);
    }

    [Fact]
    public void Failure_carries_error_and_default_value()
    {
        var result = Result<string>.Failure(Error.NotFound("patient"));

        result.IsFailure.Should().BeTrue();
        result.Value.Should().BeNull();
        result.Error.Code.Should().Be("not_found");
        result.Error.Message.Should().Be("patient not found");
    }

    [Fact]
    public void Implicit_conversion_from_value_is_success()
    {
        Result<string> result = "ok";

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ok");
    }

    [Fact]
    public void Implicit_conversion_from_error_is_failure()
    {
        Result<string> result = Error.Validation("bad input");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("validation");
    }

    [Fact]
    public void NonGeneric_result_success_and_failure()
    {
        Result.Success().IsSuccess.Should().BeTrue();
        Result.Failure(Error.Conflict("dup")).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Error_factories_produce_stable_codes()
    {
        Error.NotFound("x").Code.Should().Be("not_found");
        Error.Validation("x").Code.Should().Be("validation");
        Error.Conflict("x").Code.Should().Be("conflict");
        Error.Unauthorized().Code.Should().Be("unauthorized");
        Error.Failure("x").Code.Should().Be("failure");
    }
}
