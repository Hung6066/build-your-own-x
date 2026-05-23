namespace Hope.Agent.Shared;

public readonly record struct Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);
    public static Error NotFound(string resource) => new("not_found", $"{resource} not found");
    public static Error Validation(string message) => new("validation", message);
    public static Error Conflict(string message) => new("conflict", message);
    public static Error Unauthorized(string message = "unauthorized") => new("unauthorized", message);
    public static Error Failure(string message) => new("failure", message);
}
