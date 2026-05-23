namespace Hope.Agent.Shared;

public readonly struct Result<T>
{
    public T? Value { get; }
    public Error Error { get; }
    public bool IsSuccess => Error.Code.Length == 0;
    public bool IsFailure => !IsSuccess;

    private Result(T? value, Error error)
    {
        Value = value;
        Error = error;
    }

    public static Result<T> Success(T value) => new(value, Error.None);
    public static Result<T> Failure(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Failure(error);
}

public readonly struct Result
{
    public Error Error { get; }
    public bool IsSuccess => Error.Code.Length == 0;
    public bool IsFailure => !IsSuccess;

    private Result(Error error) => Error = error;

    public static Result Success() => new(Error.None);
    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => Failure(error);
}
