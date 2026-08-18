using System.Diagnostics.Metrics;
using System.Net;
using FirmaData.Api.Observability;
using FluentAssertions;
using Polly;
using Polly.CircuitBreaker;

namespace FirmaData.Api.IntegrationTests;

public class DependencyMetricsHandlerTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed record Measurement(string Instrument, object Value, IReadOnlyDictionary<string, object?> Tags);

    private static async Task<IReadOnlyList<Measurement>> CaptureAsync(Func<HttpClient, Task> act, string dependency = "cvr", Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
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

        var handler = new DependencyMetricsHandler(dependency)
        {
            InnerHandler = new StubHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK))),
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await act(client);

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
    public async Task SendAsync_OnSuccess_RecordsSuccessOutcome()
    {
        var measurements = await CaptureAsync(
            client => client.GetAsync("api/v1/16500836"),
            respond: _ => new HttpResponseMessage(HttpStatusCode.OK));

        var counter = measurements.Single(m => m.Instrument == "firmadata.dependency.requests");
        counter.Tags["dependency"].Should().Be("cvr");
        counter.Tags["operation"].Should().Be("lookup");
        counter.Tags["outcome"].Should().Be("success");
        measurements.Should().Contain(m => m.Instrument == "firmadata.dependency.duration");
    }

    [Fact]
    public async Task SendAsync_OnClientError_RecordsClientErrorOutcome()
    {
        var measurements = await CaptureAsync(
            client => client.GetAsync("api/v1/16500836"),
            respond: _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var counter = measurements.Single(m => m.Instrument == "firmadata.dependency.requests");
        counter.Tags["outcome"].Should().Be("client_error");
    }

    [Fact]
    public async Task SendAsync_OnServerError_RecordsServerErrorOutcome()
    {
        var measurements = await CaptureAsync(
            client => client.GetAsync("api/v1/16500836"),
            respond: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var counter = measurements.Single(m => m.Instrument == "firmadata.dependency.requests");
        counter.Tags["outcome"].Should().Be("server_error");
    }

    [Fact]
    public async Task SendAsync_ClassifiesSearchPath()
    {
        var measurements = await CaptureAsync(client => client.GetAsync("api/v1/search/company/lb"));

        var counter = measurements.Single(m => m.Instrument == "firmadata.dependency.requests");
        counter.Tags["operation"].Should().Be("search");
    }

    [Fact]
    public async Task SendAsync_ClassifiesStatbankDataPath()
    {
        var measurements = await CaptureAsync(client => client.PostAsync("v1/data", null), dependency: "statbank");

        var counter = measurements.Single(m => m.Instrument == "firmadata.dependency.requests");
        counter.Tags["dependency"].Should().Be("statbank");
        counter.Tags["operation"].Should().Be("statistics");
    }

    [Fact]
    public async Task SendAsync_ClassifiesTableinfoPath()
    {
        var measurements = await CaptureAsync(client => client.GetAsync("v1/tableinfo?id=ERHV1"), dependency: "statbank");

        var counter = measurements.Single(m => m.Instrument == "firmadata.dependency.requests");
        counter.Tags["operation"].Should().Be("years");
    }

    [Fact]
    public async Task SendAsync_WhenCircuitIsOpen_RecordsCircuitOpenOutcomeAndRethrows()
    {
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Result?.StatusCode == HttpStatusCode.InternalServerError),
            })
            .Build();

        // Trip the circuit directly against the pipeline first, no HTTP involved.
        for (var i = 0; i < 2; i++)
        {
            await pipeline.ExecuteAsync(static _ => new ValueTask<HttpResponseMessage>(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        }

        var measurements = new List<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "FirmaData")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));
        listener.Start();

        var handler = new DependencyMetricsHandler("cvr")
        {
            InnerHandler = new ResilienceExecutingHandler(pipeline, _ => new HttpResponseMessage(HttpStatusCode.OK)),
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        var act = () => client.GetAsync("api/v1/16500836");

        await act.Should().ThrowAsync<BrokenCircuitException>();
        var counter = measurements.Single(m => m.Instrument == "firmadata.dependency.requests");
        counter.Tags["outcome"].Should().Be("circuit_open");
    }

    private sealed class ResilienceExecutingHandler(ResiliencePipeline<HttpResponseMessage> pipeline, Func<HttpRequestMessage, HttpResponseMessage> respond) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            await pipeline.ExecuteAsync(_ => new ValueTask<HttpResponseMessage>(respond(request)), cancellationToken);
    }
}
