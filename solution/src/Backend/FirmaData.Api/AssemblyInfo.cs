using System.Runtime.CompilerServices;

// Lets FirmaData.Api.IntegrationTests unit-test internal types directly (e.g.
// DependencyMetricsHandler's outcome classification) without making them public API.
[assembly: InternalsVisibleTo("FirmaData.Api.IntegrationTests")]
