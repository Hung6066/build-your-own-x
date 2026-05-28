using System.ComponentModel.DataAnnotations;

namespace Hope.Agent.Api.Middleware;

/// <summary>
/// Endpoint filter that enforces DataAnnotations (<see cref="RequiredAttribute"/>,
/// <see cref="StringLengthAttribute"/>, <see cref="RangeAttribute"/>, etc.) on
/// every <c>[FromBody]</c> argument before the handler is invoked.
/// Minimal API handlers do NOT auto-validate annotations — this filter bridges that gap.
/// Returns 400 <see cref="HttpValidationProblemDetails"/> on the first failing argument.
/// </summary>
internal static class ValidationFilterExtensions
{
    public static TBuilder WithRequestValidation<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilterFactory(static (_, next) => async ctx =>
        {
            foreach (var arg in ctx.Arguments)
            {
                if (arg is null) continue;
                // Primitive types (string, int, …) have no class-level constraints to check.
                if (arg.GetType().IsPrimitive || arg is string) continue;

                var results = new List<ValidationResult>();
                var vc = new ValidationContext(arg);
                if (!Validator.TryValidateObject(arg, vc, results, validateAllProperties: true))
                {
                    var errors = results
                        .GroupBy(r => r.MemberNames.FirstOrDefault() ?? string.Empty)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(r => r.ErrorMessage ?? "Invalid value.").ToArray());
                    return Results.ValidationProblem(errors);
                }
            }
            return await next(ctx);
        });
}
