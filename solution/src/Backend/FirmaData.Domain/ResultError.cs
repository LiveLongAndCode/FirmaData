namespace FirmaData.Domain;

public enum ResultErrorType
{
    Validation,
    NotFound,
    Unavailable,
    Unexpected,

    // A more specific NotFound: the requested industry code is syntactically valid but not one
    // Statbank's ERHV1 (DB07) table recognises -- e.g. a DB25-revision code CVR has started
    // issuing. Produced only by FirmaData.Statbank and consumed only by
    // ResultExtensions.ToStatus(), which turns it into EnrichmentStatus.IndustryCodeNotSupported
    // before it ever reaches the API boundary (plan fase 6, F5).
    IndustryCodeNotSupported
}

public sealed record ResultError(ResultErrorType Type, string Message)
{
    public override string ToString() => Message;
}

public static class Result
{
    public static ResultError Validation(string message) => new(ResultErrorType.Validation, message);

    public static ResultError NotFound(string message = "The requested resource was not found.") =>
        new(ResultErrorType.NotFound, message);

    public static ResultError Unavailable(string message) => new(ResultErrorType.Unavailable, message);

    public static ResultError Unexpected(string message) => new(ResultErrorType.Unexpected, message);

    public static ResultError IndustryCodeNotSupported(string message) =>
        new(ResultErrorType.IndustryCodeNotSupported, message);
}
