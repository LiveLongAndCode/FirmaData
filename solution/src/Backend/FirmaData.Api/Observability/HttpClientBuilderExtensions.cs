namespace FirmaData.Api.Observability;

internal static class HttpClientBuilderExtensions
{
    // Added before the resilience pipeline (see Program.cs composition order) so it wraps the
    // whole thing, not the other way around.
    public static IHttpClientBuilder AddDependencyMetrics(this IHttpClientBuilder builder, string dependency) =>
        builder.AddHttpMessageHandler(() => new DependencyMetricsHandler(dependency));
}
