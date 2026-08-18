namespace FirmaData.Domain;

public enum EnrichmentStatus
{
    Ok,
    NotAvailableForYear,
    SourceUnavailable,

    // The industry code is valid but Statbank's ERHV1 (DB07) table doesn't recognise it -- e.g. a
    // DB25-revision code CVR has started issuing. Distinct from NotAvailableForYear (the year is
    // the problem) and SourceUnavailable (Statbank itself is unreachable): this is a permanent,
    // deterministic classification drift between the two source systems (plan fase 6, F5).
    IndustryCodeNotSupported
}
