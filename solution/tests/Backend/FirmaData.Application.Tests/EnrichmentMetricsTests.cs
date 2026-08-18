using System.Diagnostics.Metrics;
using FirmaData.Domain;
using FluentAssertions;
using NSubstitute;

namespace FirmaData.Application.Tests;

// Observes the "FirmaData" meter directly (plan section 7.2) rather than reaching into
// EnrichmentMetrics' internals -- proves what an OTel exporter would actually see.
public class EnrichmentMetricsTests
{
    private static readonly CvrNumber Cvr = CvrNumber.TryCreate("16500836").Value;
    private static readonly IndustryCode Erhv651200 = IndustryCode.TryCreate("651200").Value;
    private static readonly StatisticsYear Year2022 = StatisticsYear.TryCreate(2022).Value;

    private static Company LbForsikring => new(
        Cvr, "LB FORSIKRING A/S", new Address("Amerika Plads 15", "2100", "København Ø"),
        Erhv651200, "Anden forsikring", 1010, CompanyStatus.Active);

    private sealed record Measurement(string Instrument, object Value, IReadOnlyDictionary<string, object?> Tags);

    private static async Task<IReadOnlyList<Measurement>> CaptureAsync(Func<Task> act)
    {
        var measurements = new List<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "FirmaData")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));
        listener.Start();

        await act();

        return measurements;
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            dictionary[tag.Key] = tag.Value;
        }

        return dictionary;
    }

    [Fact]
    public async Task EnrichByCvrAsync_WhenOk_RecordsDurationButNotDegraded()
    {
        var directory = Substitute.For<ICompanyDirectory>();
        var statistics = Substitute.For<IIndustryStatisticsProvider>();
        directory.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(LbForsikring);
        statistics.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>())
            .Returns(new IndustryStatistics(Erhv651200, Year2022, 166, 15206, 13458, 10380));
        var sut = new CompanyEnrichmentService(directory, statistics);

        var measurements = await CaptureAsync(() => sut.EnrichByCvrAsync(Cvr, Year2022, CancellationToken.None));

        var duration = measurements.Single(m => m.Instrument == "firmadata.enrichment.duration");
        duration.Tags["lookup"].Should().Be("cvr");
        measurements.Should().NotContain(m => m.Instrument == "firmadata.enrichment.degraded");
    }

    [Fact]
    public async Task EnrichByCvrAsync_WhenDegraded_RecordsDegradedWithReason()
    {
        var directory = Substitute.For<ICompanyDirectory>();
        var statistics = Substitute.For<IIndustryStatisticsProvider>();
        directory.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(LbForsikring);
        statistics.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>())
            .Returns(Result.Unavailable("Statbank is down."));
        var sut = new CompanyEnrichmentService(directory, statistics);

        var measurements = await CaptureAsync(() => sut.EnrichByCvrAsync(Cvr, Year2022, CancellationToken.None));

        var degraded = measurements.Single(m => m.Instrument == "firmadata.enrichment.degraded");
        degraded.Tags["reason"].Should().Be(nameof(EnrichmentStatus.SourceUnavailable));
    }

    [Fact]
    public async Task SearchAndEnrichAsync_RecordsDurationTaggedByName()
    {
        var directory = Substitute.For<ICompanyDirectory>();
        var statistics = Substitute.For<IIndustryStatisticsProvider>();
        directory.SearchByNameAsync("lb", Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<Company>>.Success([]));
        var sut = new CompanyEnrichmentService(directory, statistics);

        var measurements = await CaptureAsync(() => sut.SearchAndEnrichAsync("lb", Year2022, 10, CancellationToken.None));

        var duration = measurements.Single(m => m.Instrument == "firmadata.enrichment.duration");
        duration.Tags["lookup"].Should().Be("name");
    }
}
