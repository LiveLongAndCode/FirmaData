using System.Net;
using FluentAssertions;
using Polly;
using Polly.CircuitBreaker;

namespace FirmaData.Api.IntegrationTests;

// Proves the circuit-breaker configuration from plan section 6.1 in isolation: FailureRatio 0.5,
// MinimumThroughput 10, SamplingDuration 30s, BreakDuration 30s -- the same values
// FirmaData.Cvr/FirmaData.Statbank's ServiceCollectionExtensions configure on the real typed
// clients. Deliberately not routed through the full API + WireMock: with the production Retry
// (3 attempts, exponential backoff) wrapping the breaker, a single failing outer call can burn
// close to the 15s total-request-timeout in backoff alone, making a real, wall-clock end-to-end
// trip of the breaker impractically slow for a test. Ten rapid in-memory executions land well
// inside the 30s sampling window on the real clock, so no fake time provider is needed either.
public class CircuitBreakerResilienceTests
{
    private static ResiliencePipeline<HttpResponseMessage> BuildPipeline() =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Result?.IsSuccessStatusCode == false),
            })
            .Build();

    [Fact]
    public async Task Breaker_OpensAfterTenFailingRequests_AndFailsFastAfterward()
    {
        var pipeline = BuildPipeline();

        // Ten failing calls -- the plan's own exit criterion -- is enough throughput at a 100%
        // failure ratio to trip a 50%-ratio/10-minimum-throughput breaker.
        for (var i = 0; i < 10; i++)
        {
            var response = await pipeline.ExecuteAsync(
                static async ct =>
                {
                    await Task.Yield();
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                },
                CancellationToken.None);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        // The next call must fail fast -- BrokenCircuitException, without ever invoking the
        // delegate, so no real HTTP call and no real timeout budget is spent waiting it out.
        var invoked = false;
        var act = () => pipeline.ExecuteAsync(
            async ct =>
            {
                invoked = true;
                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<BrokenCircuitException>();
        invoked.Should().BeFalse();
    }
}
