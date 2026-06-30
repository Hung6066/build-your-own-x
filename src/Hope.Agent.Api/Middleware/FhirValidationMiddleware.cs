using System.Text.Json;
using Hope.Agent.Application.Fhir;
using Microsoft.AspNetCore.Http;

namespace Hope.Agent.Api.Middleware;

/// <summary>
/// ASP.NET Core middleware that validates incoming FHIR resource payloads.
/// Closes gap H-1. Maps POST /v1/fhir/{resourceType} requests to FHIR R4 validation.
/// </summary>
public sealed class FhirValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IFhirValidator _validator;

    public FhirValidationMiddleware(RequestDelegate next, IFhirValidator validator)
    {
        _next = next;
        _validator = validator;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/v1/fhir", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var resourceType = context.Request.RouteValues["resourceType"]?.ToString();
        if (string.IsNullOrWhiteSpace(resourceType))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "missing_resource_type",
                message = "FHIR endpoint requires {resourceType} in path, e.g. /v1/fhir/Patient"
            });
            return;
        }

        if (!_validator.SupportedResourceTypes.Contains(resourceType))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "unsupported_resource_type",
                message = $"Resource type '{resourceType}' is not supported. Supported: {string.Join(", ", _validator.SupportedResourceTypes)}"
            });
            return;
        }

        // Read and validate the FHIR payload
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;

        JsonDocument? payload = null;
        try
        {
            payload = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid_json",
                message = "Request body is not valid JSON"
            });
            return;
        }

        using (payload)
        {
            var result = await _validator.ValidateAsync(resourceType, payload);

            if (!result.IsValid)
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "fhir_validation_failed",
                    resourceType,
                    result.Errors,
                    result.Warnings
                });
                return;
            }
        }

        // Payload is valid — pass to next middleware
        await _next(context);
    }
}

/// <summary>Extension method to register FHIR validation middleware.</summary>
public static class FhirValidationMiddlewareExtensions
{
    public static IApplicationBuilder UseFhirValidation(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<FhirValidationMiddleware>();
    }
}
