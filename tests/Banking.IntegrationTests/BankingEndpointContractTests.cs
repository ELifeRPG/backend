using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace ELifeRPG.Banking.IntegrationTests;

/// <summary>
/// The 409s on the booking endpoints tell the Bridge whether resending can help. Pure functions, so
/// no provider or database; they live here because Banking.Api grants this assembly InternalsVisibleTo.
/// </summary>
public sealed class BankingEndpointContractTests
{
    [Fact]
    public void InsufficientBalance_IsNotRetryable()
    {
        var problem = Assert.IsType<ProblemHttpResult>(BankingModule.InsufficientBalanceProblem()).ProblemDetails;

        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal("insufficient_balance", problem.Extensions["code"]);
        Assert.Equal(false, problem.Extensions["retryable"]);
    }

    [Fact]
    public void ConcurrentModification_IsRetryable()
    {
        var problem = Assert.IsType<ProblemHttpResult>(BankingModule.ConcurrentModificationProblem()).ProblemDetails;

        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal("concurrent_modification", problem.Extensions["code"]);
        Assert.Equal(true, problem.Extensions["retryable"]);
    }

    [Fact]
    public void IdempotencyKeyReused_Is422AndNotRetryable()
    {
        var result = Assert.IsType<ProblemHttpResult>(BankingModule.IdempotencyKeyReusedProblem());

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        Assert.Equal("idempotency_key_reused", result.ProblemDetails.Extensions["code"]);
        Assert.Equal(false, result.ProblemDetails.Extensions["retryable"]);
    }

    [Fact]
    public void InvalidIdempotencyKey_Is400()
    {
        var result = Assert.IsType<ProblemHttpResult>(BankingModule.InvalidIdempotencyKeyProblem());

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }
}

