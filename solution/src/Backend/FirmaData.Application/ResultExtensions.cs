using FirmaData.Domain;

namespace FirmaData.Application;

internal static class ResultExtensions
{
    // Maps a statistics lookup's outcome onto the degradation matrix (plan section 6.3): a
    // "not found" (deterministic 400 / all-zero sentinel, see FirmaData.Statbank) means the year
    // has no data, while any other failure means the source itself was unreachable.
    public static EnrichmentStatus ToStatus(this Result<IndustryStatistics> result) => result switch
    {
        { IsSuccess: true } => EnrichmentStatus.Ok,
        { Error.Type: ResultErrorType.NotFound } => EnrichmentStatus.NotAvailableForYear,
        _ => EnrichmentStatus.SourceUnavailable,
    };
}
