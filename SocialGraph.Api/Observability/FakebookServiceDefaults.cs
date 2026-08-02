using System.Globalization;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.DependencyInjection;

internal static class FakebookServiceDefaults
{
    public static IServiceCollection AddFakebookServiceDefaults(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var sampleRatio = ReadSampleRatio(configuration);
        var endpoint = ReadOtlpEndpoint(configuration);
        var telemetry = services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService(
            serviceName, serviceVersion: typeof(FakebookServiceDefaults).Assembly.GetName().Version?.ToString(), serviceInstanceId: Environment.MachineName));
        telemetry.WithTracing(tracing =>
        {
            tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio)))
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage = request => request.RequestUri?.AbsolutePath.StartsWith("/health", StringComparison.OrdinalIgnoreCase) != true);
            if (endpoint is not null) tracing.AddOtlpExporter(options => options.Endpoint = endpoint);
        });
        telemetry.WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation();
            if (endpoint is not null) metrics.AddOtlpExporter(options => options.Endpoint = endpoint);
        });
        // Safe methods may be retried by the shared resilience pipeline; unsafe
        // mutations are deliberately left to the signed outbox/idempotency flow.
        services.ConfigureHttpClientDefaults(http =>
            http.AddStandardResilienceHandler(options => options.Retry.DisableForUnsafeHttpMethods()));
        return services;
    }
    private static double ReadSampleRatio(IConfiguration configuration) =>
        double.TryParse(configuration["Observability:TraceSampleRatio"] ?? configuration["OTEL_TRACES_SAMPLER_ARG"], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, 0d, 1d) : 0.1d;
    private static Uri? ReadOtlpEndpoint(IConfiguration configuration) =>
        Uri.TryCreate(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? configuration["OpenTelemetry:OtlpEndpoint"], UriKind.Absolute, out var endpoint) ? endpoint : null;
}
