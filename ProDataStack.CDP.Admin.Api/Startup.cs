namespace ProDataStack.CDP.Admin.Api
{
    using System.Net.Http.Headers;
    using Azure.Monitor.OpenTelemetry.AspNetCore;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using OpenTelemetry.Metrics;
    using OpenTelemetry.Trace;
    using ProDataStack.Chassis.Authentication;
    using ProDataStack.Chassis.DependencyInjection;
    using ProDataStack.CDP.Admin.Api.Services;
    using ProDataStack.CDP.TenantCatalog.Context;

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

            // Tenant catalog database
            services.AddDbContextFactory<TenantCatalogDbContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionString("TenantCatalog")));

            // Clerk Organization API (REST client — SDK v0.13 lacks org support)
            var clerkSecretKey = Configuration["Clerk:SecretKey"] ?? "";
            services.AddHttpClient<ClerkOrganizationService>(client =>
            {
                client.BaseAddress = new Uri("https://api.clerk.com/v1/");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clerkSecretKey);
            });

            // GitHub API for triggering provisioning pipeline
            var githubToken = Configuration["GitHub:Token"] ?? "";
            services.AddHttpClient<ProvisioningService>(client =>
            {
                client.BaseAddress = new Uri("https://api.github.com/");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                client.DefaultRequestHeaders.UserAgent.ParseAdd("CDP-Admin-API/1.0");
            });

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
