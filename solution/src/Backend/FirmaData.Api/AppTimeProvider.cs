namespace FirmaData.Api;

// The key this app's TimeProvider is registered under (plan fase 7, F9c) -- deliberately a keyed
// service rather than the unkeyed TimeProvider registration. Microsoft.Extensions.Http.Resilience
// resolves an unkeyed TimeProvider from the same container to drive Polly's own retry/timeout
// delays; substituting a frozen FakeTimeProvider there (as tests do for this key) would stall
// every scheduled retry indefinitely. See Program.cs and ApiFactory for the two registrations.
internal static class AppTimeProvider
{
    public const string ServiceKey = "app";
}
