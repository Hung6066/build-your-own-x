using System.Security.Claims;
using Hope.Agent.Application.Security;

namespace Hope.Agent.Api.Middleware;

/// <summary>
/// Endpoint filter that requires a valid RFC 9449 DPoP proof header (<c>DPoP</c>)
/// matching the access token's <c>cnf.jkt</c> claim. Opt-in per endpoint via
/// <see cref="WithDpop{TBuilder}"/>. Closes gap C1 — even a stolen bearer token
/// cannot be replayed without the client's private key.
/// </summary>
public static class DpopFilterExtensions
{
    public const string HeaderName = "DPoP";
    public const string ConfirmationClaim = "cnf";
    public const string ThumbprintMember = "jkt";

    public static TBuilder WithDpop<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilterFactory((ctx, next) =>
        {
            return async invocation =>
            {
                var http = invocation.HttpContext;
                var validator = http.RequestServices.GetService(typeof(IDpopValidator)) as IDpopValidator;
                if (validator is null)
                    return await next(invocation);

                if (!http.Request.Headers.TryGetValue(HeaderName, out var proof) ||
                    string.IsNullOrWhiteSpace(proof))
                    return Results.Problem(statusCode: 401, title: "missing_dpop");

                var uri = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}";
                var result = await validator.ValidateAsync(proof!, http.Request.Method, uri, http.RequestAborted);
                if (!result.IsValid)
                    return Results.Problem(statusCode: 401, title: $"invalid_dpop:{result.Reason}");

                // Bind: the access token must declare the same thumbprint in cnf.jkt.
                var expected = ExtractJkt(http.User);
                if (string.IsNullOrEmpty(expected))
                    return Results.Problem(statusCode: 401, title: "token_not_bound");
                if (!string.Equals(expected, result.Thumbprint, StringComparison.Ordinal))
                    return Results.Problem(statusCode: 401, title: "thumbprint_mismatch");

                return await next(invocation);
            };
        });
        return builder;
    }

    private static string? ExtractJkt(ClaimsPrincipal user)
    {
        var cnf = user.FindFirst(ConfirmationClaim)?.Value;
        if (string.IsNullOrWhiteSpace(cnf)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(cnf);
            return doc.RootElement.TryGetProperty(ThumbprintMember, out var jkt) ? jkt.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
