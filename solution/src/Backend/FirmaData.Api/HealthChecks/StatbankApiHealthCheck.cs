using FirmaData.Statbank;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FirmaData.Api.HealthChecks;

// A cheap reachability probe, not a full lookup (plan section 7.4). Statistics are an enrichment
// source, not the core lookup -- the service can still serve master data without it, so this
// reports Degraded rather than Unhealthy, matching the degradation matrix (section 6.3) applied
// at the infrastructure level: an orchestrator should not kill or depool the service over this.
internal sealed class StatbankApiHealthCheck(IHttpClientFactory httpClientFactory, IOptions<StatbankOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(3);

        try
        {
            using var response = await client.GetAsync(new Uri(options.Value.BaseUrl), cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Degraded("The Statbank API is unreachable.", ex);
        }
    }
}
