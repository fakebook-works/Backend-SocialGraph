namespace SocialGraph.Api.Infrastructure;

using System.Text.Json;

public sealed class InternalApiAuthenticationMiddleware
{
    public const string SecretHeaderName = "X-Gateway-Secret";

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;

    public InternalApiAuthenticationMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/internal"))
        {
            await _next(context);
            return;
        }

        var authentication = InternalCallerAuthentication.Validate(_configuration, context.Request.Headers);
        if (authentication == InternalAuthenticationResult.NotConfigured)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "INTERNAL_AUTH_NOT_CONFIGURED",
                "Internal service authentication is not configured.");
            return;
        }

        if (authentication != InternalAuthenticationResult.Valid)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                "Internal service authentication failed.");
            return;
        }

        await _next(context);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new { error = new { code, message } },
            cancellationToken: context.RequestAborted);
    }
}
