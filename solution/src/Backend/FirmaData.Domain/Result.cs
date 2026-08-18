namespace FirmaData.Domain;

public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly ResultError? _error;

    private Result(T value)
    {
        _value = value;
        _error = null;
        IsSuccess = true;
    }

    private Result(ResultError error)
    {
        _value = default;
        _error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot access Value on a failed result. Error: {_error}");

    public ResultError Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Cannot access Error on a successful result.");

    public T? ValueOrDefault => _value;

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(ResultError error) => new(error);

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(ResultError error) => new(error);
}
