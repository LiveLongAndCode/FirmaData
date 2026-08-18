namespace FirmaData.Statbank;

// Resilience budgets for the Statbank client (plan fase 7, F8), config-bound under
// "Statbank:Resilience". Defaults match the values that were previously hardcoded in
// ServiceCollectionExtensions, so behaviour is unchanged when the section is absent.
public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    public int TotalTimeoutSeconds { get; init; } = 15;

    public int AttemptTimeoutSeconds { get; init; } = 5;

    public int MaxRetryAttempts { get; init; } = 3;

    public double CircuitFailureRatio { get; init; } = 0.5;

    public int CircuitMinimumThroughput { get; init; } = 10;

    public int CircuitSamplingDurationSeconds { get; init; } = 30;

    public int CircuitBreakDurationSeconds { get; init; } = 30;
}
