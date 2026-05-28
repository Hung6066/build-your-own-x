using Microsoft.AspNetCore.Http.Features;

namespace Hope.Agent.Api.Middleware;

/// <summary>
/// Endpoint convention extensions for enforcing per-group request body size limits.
/// Uses <see cref="IHttpMaxRequestBodySizeFeature"/> so Kestrel enforces the limit
/// before the body reaches model binding or the handler, providing defence against
/// memory-pressure DoS via oversized payloads.
/// </summary>
internal static class BodyPolicyExtensions
{
    /// <summary>
    /// Sets the maximum allowed request body size for all endpoints in this group or route.
    /// Must be called BEFORE the Kestrel global limit is hit; Kestrel enforces whichever is lower.
    /// </summary>
    /// <param name="builder">The route group or handler builder to configure.</param>
    /// <param name="maxBytes">Maximum body size in bytes.</param>
    internal static TBuilder WithBodySizeLimit<TBuilder>(this TBuilder builder, long maxBytes)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            // IHttpMaxRequestBodySizeFeature.IsReadOnly is true once the body starts
            // being read; this filter runs before model binding so it is always writable.
            var feature = ctx.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
                feature.MaxRequestBodySize = maxBytes;

            return await next(ctx);
        });

        return builder;
    }
}
