using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Adds the cross-cutting concerns shared by every service in the distributed application.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>
    /// Configures OpenTelemetry, service discovery and resilient HTTP defaults.
    /// </summary>
    /// <typeparam name="TBuilder">The host application builder type.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The supplied <paramref name="builder"/>.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureOpenTelemetry();
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Configures OpenTelemetry logging, metrics and tracing, exporting through OTLP when configured.
    /// </summary>
    /// <typeparam name="TBuilder">The host application builder type.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The supplied <paramref name="builder"/>.</returns>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter("Orb.Automate.Agents.HR.ServiceNow")
                // Literal names are required because ServiceDefaults cannot reference the API project.
                // These must match ServiceNowDiagnostics.Name and ChatAiMetrics.MeterName.
                .AddMeter("Orb.Automate.Agents.HR.ChatAI")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource("Microsoft.Extensions.AI", "Microsoft.Agents.AI", "Orb.Automate.Agents.HR.ServiceNow")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage = static request =>
                    request.RequestUri?.Query.Contains("upn=", StringComparison.OrdinalIgnoreCase) != true));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}
