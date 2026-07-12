namespace SocialGraph.Api.Infrastructure;

using System.Security.Cryptography;
using System.Text;
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

        var expectedSecret = _configuration["Gateway:InternalSharedSecret"] ??
                             _configuration["InternalServices:SharedSecret"] ??
                             string.Empty;
        if (Encoding.UTF8.GetByteCount(expectedSecret) < 32)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "INTERNAL_AUTH_NOT_CONFIGURED",
                "Internal service authentication is not configured.");
            return;
        }

        var providedSecret = context.Request.Headers[SecretHeaderName].ToString();
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSecret);
        var providedBytes = Encoding.UTF8.GetBytes(providedSecret);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
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
