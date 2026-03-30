namespace ProDataStack.CDP.Admin.Api
{
    using Azure.Monitor.OpenTelemetry.AspNetCore;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using OpenTelemetry.Metrics;
    using OpenTelemetry.Trace;
    using ProDataStack.Chassis.Authentication;
    using ProDataStack.Chassis.DependencyInjection;

    public class Startup : ProDataStack.Chassis.StartupBase
    {
        public Startup(IConfiguration configuration)
            : base(configuration)
        {
        }

        public override void ConfigureComponent(IApplicationBuilder app)
        {
        }

        public override void ConfigureComponentServices(IServiceCollection services)
        {
            services.AddChassisAuthentication(Configuration);
            services.AddAMQP(Configuration);

            services.AddHealthChecks();

            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

            services.AddApplicationInsightsTelemetry(options =>
            {
                options.EnableAdaptiveSampling = false;
            });

            var otelBuilder = services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation();
                    metrics.AddHttpClientInstrumentation();
                    metrics.AddRuntimeInstrumentation();
                })
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation();
                    tracing.AddHttpClientInstrumentation();
                });

            if (!env.Equals("Development", StringComparison.OrdinalIgnoreCase))
            {
                otelBuilder.UseAzureMonitor();
            }
        }
    }
}
