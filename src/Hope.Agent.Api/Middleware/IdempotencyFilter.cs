using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Hope.Agent.Application.Security;
using Microsoft.AspNetCore.Http.Features;

namespace Hope.Agent.Api.Middleware;

/// <summary>
/// Endpoint filter that implements <a href="https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/">
/// the IETF "Idempotency-Key" HTTP header draft</a> for unsafe HTTP methods
/// (POST / PUT / PATCH / DELETE).
///
/// <para>Behaviour:</para>
/// <list type="bullet">
///   <item>If no <c>Idempotency-Key</c> header is present, the request runs normally
///         (header is opt-in, matching Stripe / GitHub semantics).</item>
///   <item>If present, the request body is hashed and a slot reserved in
///         <see cref="IIdempotencyStore"/> keyed on (userId, key).</item>
///   <item>Concurrent retry → <b>409 Conflict</b> + <c>Retry-After: 5</c>.</item>
///   <item>Replay with matching body → cached response is returned verbatim
///         (handler is NOT re-invoked — guarantees no double-admit, no duplicate billing).</item>
///   <item>Same key with different body → <b>422 Unprocessable Entity</b>
///         (clients must not reuse keys across distinct operations).</item>
/// </list>
/// </summary>
internal static class IdempotencyFilterExtensions
{
    private const string HeaderName = "Idempotency-Key";
    private const int MaxKeyLength = 255;

    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilterFactory(static (_, next) => async ctx =>
        {
            var http = ctx.HttpContext;
            var method = http.Request.Method;

            // Idempotency only applies to write methods.
            if (!HttpMethods.IsPost(method)
                && !HttpMethods.IsPut(method)
                && !HttpMethods.IsPatch(method)
                && !HttpMethods.IsDelete(method))
                return await next(ctx);

            if (!http.Request.Headers.TryGetValue(HeaderName, out var hdr) || hdr.Count == 0)
                return await next(ctx); // header is opt-in

            var key = hdr.ToString();
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength)
                return Results.Problem(
                    title: "Invalid Idempotency-Key",
                    detail: $"Header must be 1–{MaxKeyLength} ASCII chars.",
                    statusCode: StatusCodes.Status400BadRequest);

            var store = http.RequestServices.GetRequiredService<IIdempotencyStore>();
            var userId = ResolveUserId(http.User);

            // Buffer body so it can be re-read by the handler after hashing.
            http.Request.EnableBuffering();
            var bodyHash = await HashRequestBodyAsync(http.Request.Body, http.RequestAborted);
            http.Request.Body.Position = 0;

            var decision = await store.TryBeginAsync(key, userId, bodyHash, http.RequestAborted);
            switch (decision)
            {
                case IdempotencyDecision.InProgress:
                    http.Response.Headers.RetryAfter = "5";
                    return Results.Problem(
                        title: "Request already in progress",
                        detail: "A request with this Idempotency-Key is still being processed.",
                        statusCode: StatusCodes.Status409Conflict);

                case IdempotencyDecision.Mismatch:
                    return Results.Problem(
                        title: "Idempotency-Key reuse with different payload",
                        detail: "This key has already been used for a different request body.",
                        statusCode: StatusCodes.Status422UnprocessableEntity);

                case IdempotencyDecision.Replay replay:
                    http.Response.StatusCode = replay.Status;
                    http.Response.Headers["Idempotent-Replayed"] = "true";
                    if (replay.Body.Length > 0)
                    {
                        http.Response.ContentType = "application/json";
                        await http.Response.Body.WriteAsync(replay.Body, http.RequestAborted);
                    }
                    return Results.Empty;
            }

            // Proceed: run the handler, capture the response body, persist it.
            var originalBody = http.Response.Body;
            using var capture = new MemoryStream();
            http.Response.Body = capture;
            try
            {
                var result = await next(ctx);
                // For Minimal-API filters, IResult is executed AFTER the filter returns
                // when we return it via `result`. To capture the body here, we must
                // execute it ourselves and return Results.Empty.
                if (result is IResult ir)
                    await ir.ExecuteAsync(http);

                capture.Position = 0;
                var bytes = capture.ToArray();
                await store.CompleteAsync(
                    key, userId, http.Response.StatusCode, bodyHash, bytes, http.RequestAborted);

                // Copy captured body to the real response stream.
                http.Response.Body = originalBody;
                if (bytes.Length > 0)
                    await originalBody.WriteAsync(bytes, http.RequestAborted);

                return Results.Empty;
            }
            catch
            {
                // Release the slot so the client may retry — propagate the exception
                // so SafeExceptionHandler can shape the response.
                await store.AbortAsync(key, userId, CancellationToken.None);
                http.Response.Body = originalBody;
                throw;
            }
        });

    private static Guid ResolveUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }

    private static async Task<string> HashRequestBodyAsync(Stream body, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
