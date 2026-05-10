using Microsoft.Extensions.Configuration;
using OpenTelemetry.Trace;

namespace Shared;

public static class OpenTelemetryTracingExtensions
{
    /// <summary>OTLP plus optional Zipkin when <c>Observability:ZipkinEndpoint</c> is set (full URL, e.g. <c>http://zipkin:9411/api/v2/spans</c>).</summary>
    public static TracerProviderBuilder AddOnlineStoreTraceExporters(
        this TracerProviderBuilder tracing,
        IConfiguration configuration)
    {
        tracing.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(configuration["Observability:OtlpEndpoint"] ?? "http://jaeger:4317");
        });

        var zipkinEndpoint = configuration["Observability:ZipkinEndpoint"];
        if (!string.IsNullOrWhiteSpace(zipkinEndpoint))
        {
            tracing.AddZipkinExporter(options =>
            {
                options.Endpoint = new Uri(zipkinEndpoint);
            });
        }

        return tracing;
    }
}
