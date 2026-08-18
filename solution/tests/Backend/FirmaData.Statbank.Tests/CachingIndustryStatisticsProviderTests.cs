using FirmaData.Application;
using FirmaData.Domain;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace FirmaData.Statbank.Tests;

public class CachingIndustryStatisticsProviderTests
{
    private static readonly IndustryCode Erhv651200 = IndustryCode.TryCreate("651200").Value;
    private static readonly StatisticsYear Year2022 = StatisticsYear.TryCreate(2022).Value;

    private static IndustryStatistics Statistics() => new(Erhv651200, Year2022, 166, 15206, 13458, 10380);

    private static (IIndustryStatisticsProvider Inner, CachingIndustryStatisticsProvider Sut) CreateSut()
    {
        var inner = Substitute.For<IIndustryStatisticsProvider>();
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        return (inner, new CachingIndustryStatisticsProvider(inner, cache));
    }

    [Fact]
    public async Task GetAsync_OnCacheMiss_CallsInnerAndReturnsItsResult()
    {
        var (inner, sut) = CreateSut();
        inner.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>()).Returns(Statistics());

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Statistics());
    }

    [Fact]
    public async Task GetAsync_OnCacheHit_DoesNotCallInnerAgain()
    {
        var (inner, sut) = CreateSut();
        inner.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>()).Returns(Statistics());

        await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);
        await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        await inner.Received(1).GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_WithNotFoundResult_CachesTheNegativeResultToo()
    {
        // A definitive "no statistics for that year" is cached briefly (plan section 6.2) --
        // it's still a definitive answer, not a transient failure.
        var (inner, sut) = CreateSut();
        inner.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>())
            .Returns(Result.NotFound("No statistics for that year."));

        await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);
        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.NotFound);
        await inner.Received(1).GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_WithIndustryCodeNotSupportedResult_CachesTheNegativeResultToo()
    {
        // Just as definitive as NotFound -- a DB07/DB25 classification mismatch doesn't change
        // within the negative-cache window (plan fase 6, F5).
        var (inner, sut) = CreateSut();
        inner.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>())
            .Returns(Result.IndustryCodeNotSupported("Industry code not recognised."));

        await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);
        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.IndustryCodeNotSupported);
        await inner.Received(1).GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_WithUnavailableResult_DoesNotCacheIt()
    {
        // A transient outage is left to the resilience pipeline to retry on the next call, not
        // remembered as fact for minutes or hours.
        var (inner, sut) = CreateSut();
        inner.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>())
            .Returns(Result.Unavailable("Statbank is down."));

        await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);
        await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        await inner.Received(2).GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_DifferentYear_IsATrulyDifferentCacheKey()
    {
        var (inner, sut) = CreateSut();
        var year2021 = StatisticsYear.TryCreate(2021).Value;
        inner.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>()).Returns(Statistics());
        inner.GetAsync(Erhv651200, year2021, Arg.Any<CancellationToken>()).Returns(Statistics() with { Year = year2021 });

        await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);
        await sut.GetAsync(Erhv651200, year2021, CancellationToken.None);

        await inner.Received(1).GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>());
        await inner.Received(1).GetAsync(Erhv651200, year2021, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_ConcurrentRequestsForSameKey_CoalesceOntoOneInnerCall()
    {
        var (inner, sut) = CreateSut();
        var gate = new TaskCompletionSource<Result<IndustryStatistics>>();
        inner.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>()).Returns(_ => gate.Task);

        var callers = Enumerable.Range(0, 5)
            .Select(_ => sut.GetAsync(Erhv651200, Year2022, CancellationToken.None))
            .ToList();
        await Task.Delay(50); // let every caller reach and block on the shared gate

        await inner.Received(1).GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>());

        gate.SetResult(Statistics());
        var results = await Task.WhenAll(callers);

        results.Should().OnlyContain(result => result.IsSuccess && result.Value == Statistics());
    }

    [Fact]
    public async Task GetAvailableYearsAsync_DelegatesToInner()
    {
        var (inner, sut) = CreateSut();
        inner.GetAvailableYearsAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<int>>.Success([2022, 2023]));

        var result = await sut.GetAvailableYearsAsync(CancellationToken.None);

        result.Value.Should().Equal(2022, 2023);
        await inner.Received(1).GetAvailableYearsAsync(Arg.Any<CancellationToken>());
    }
}
