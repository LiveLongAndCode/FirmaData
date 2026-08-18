namespace FirmaData.Domain;

public enum ResultErrorType
{
    Validation,
    NotFound,
    Unavailable,
    Unexpected
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
}
