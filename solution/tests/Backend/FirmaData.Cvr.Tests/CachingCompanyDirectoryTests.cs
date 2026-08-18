using FirmaData.Application;
using FirmaData.Domain;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace FirmaData.Cvr.Tests;

public class CachingCompanyDirectoryTests
{
    private static readonly CvrNumber Cvr = CvrNumber.TryCreate("16500836").Value;
    private static readonly IndustryCode Erhv651200 = IndustryCode.TryCreate("651200").Value;

    private static Company LbForsikring => new(
        Cvr, "LB FORSIKRING A/S", new Address("Amerika Plads 15", "2100", "København Ø"),
        Erhv651200, "Anden forsikring", 1010, CompanyStatus.Active);

    private static (ICompanyDirectory Inner, CachingCompanyDirectory Sut) CreateSut()
    {
        var inner = Substitute.For<ICompanyDirectory>();
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        return (inner, new CachingCompanyDirectory(inner, cache));
    }

    [Fact]
    public async Task GetByCvrAsync_OnCacheMiss_CallsInnerAndReturnsItsResult()
    {
        var (inner, sut) = CreateSut();
        inner.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(LbForsikring);

        var result = await sut.GetByCvrAsync(Cvr, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(LbForsikring);
    }

    [Fact]
    public async Task GetByCvrAsync_OnCacheHit_DoesNotCallInnerAgain()
    {
        var (inner, sut) = CreateSut();
        inner.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(LbForsikring);

        await sut.GetByCvrAsync(Cvr, CancellationToken.None);
        await sut.GetByCvrAsync(Cvr, CancellationToken.None);

        await inner.Received(1).GetByCvrAsync(Cvr, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByCvrAsync_WithNotFoundResult_CachesTheNegativeResultToo()
    {
        var (inner, sut) = CreateSut();
        inner.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(Result.NotFound("No company found."));

        await sut.GetByCvrAsync(Cvr, CancellationToken.None);
        var result = await sut.GetByCvrAsync(Cvr, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.NotFound);
        await inner.Received(1).GetByCvrAsync(Cvr, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByCvrAsync_WithUnavailableResult_DoesNotCacheIt()
    {
        // A transient outage is left to the resilience pipeline to retry on the next call, not
        // remembered as fact for minutes.
        var (inner, sut) = CreateSut();
        inner.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(Result.Unavailable("CVR API is down."));

        await sut.GetByCvrAsync(Cvr, CancellationToken.None);
        await sut.GetByCvrAsync(Cvr, CancellationToken.None);

        await inner.Received(2).GetByCvrAsync(Cvr, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchByNameAsync_IsNeverCached()
    {
        // High cardinality, low reuse, and a cache key built from a free-text search would carry
        // PII -- SearchByNameAsync is deliberately passed straight through, unlike GetByCvrAsync.
        var (inner, sut) = CreateSut();
        inner.SearchByNameAsync("lb", Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<Company>>.Success([LbForsikring]));

        await sut.SearchByNameAsync("lb", CancellationToken.None);
        await sut.SearchByNameAsync("lb", CancellationToken.None);

        await inner.Received(2).SearchByNameAsync("lb", Arg.Any<CancellationToken>());
    }
}
