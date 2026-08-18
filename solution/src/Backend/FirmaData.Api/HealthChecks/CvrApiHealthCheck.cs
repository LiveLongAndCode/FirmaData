using FirmaData.Cvr;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FirmaData.Api.HealthChecks;

// A cheap reachability probe, not a full lookup (plan section 7.4). CVR is the core master-data
// source -- if it's unreachable, the service has nothing to serve, so this reports Unhealthy.
internal sealed class CvrApiHealthCheck(
    IHttpClientFactory httpClientFactory, IOptions<CvrOptions> options, IOptions<ResilienceOptions> resilienceOptions)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(resilienceOptions.Value.HealthCheckTimeoutSeconds);

        try
        {
            using var response = await client.GetAsync(new Uri(options.Value.BaseUrl), cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("The CVR API is unreachable.", ex);
        }
    }
}
