namespace FirmaData.Contracts;

// R4: industry statistics -- workplaces, jobs, total employment (full-time equivalents), and
// wage sum, all counted across the whole industry (not the individual company) as of end of
// November, per Statbank's ERHV1 methodology (plan fase 8, F6). Distinct from
// CompanyDto.EmployeeCount, which is the company's own headcount from CVR -- the two are not
// comparable and must not be confused, which is why neither field is named plain "Employees".
// Nullable fields mirror Statbank's ".." suppressed-value marker (FirmaData.Statbank) --
// "we don't know" is not "there were none".
public sealed record IndustryStatisticsDto(
    string IndustryCode,
    int Year,
    long? WorkplacesEndNovember,
    long? JobsEndNovember,
    long? FullTimeEquivalents,
    decimal? WageSumMillionDkk);
