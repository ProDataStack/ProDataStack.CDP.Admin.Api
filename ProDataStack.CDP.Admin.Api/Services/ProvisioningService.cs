using System.Text;
using System.Text.Json;

namespace ProDataStack.CDP.Admin.Api.Services;

/// <summary>
/// Triggers the tenant provisioning pipeline via GitHub API (workflow_dispatch).
/// </summary>
public class ProvisioningService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProvisioningService> _logger;
    private readonly IConfiguration _configuration;

    public ProvisioningService(HttpClient httpClient, ILogger<ProvisioningService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task TriggerProvisioningAsync(string tenantSlug, Guid tenantId)
    {
        var environment = _configuration["Provisioning:Environment"] ?? "testing";
        var repo = _configuration["Provisioning:Repo"] ?? "ProDataStack/ProDataStack.CDP.TenantProvisioning";
        var workflowFile = _configuration["Provisioning:WorkflowFile"] ?? "provision-tenant.yml";

        var body = new
        {
            @ref = "main",
            inputs = new
            {
                tenant_slug = tenantSlug,
                tenant_id = tenantId.ToString(),
                environment
            }
        };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"repos/{repo}/actions/workflows/{workflowFile}/dispatches",
            content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("GitHub API error triggering provisioning: {Status} {Body}", response.StatusCode, error);
            throw new HttpRequestException($"Failed to trigger provisioning: {response.StatusCode}");
        }

        _logger.LogInformation("Triggered provisioning for tenant {Slug} ({Id})", tenantSlug, tenantId);
    }

    public async Task TriggerDestroyAsync(string tenantSlug)
    {
        var environment = _configuration["Provisioning:Environment"] ?? "testing";
        var repo = _configuration["Provisioning:Repo"] ?? "ProDataStack/ProDataStack.CDP.TenantProvisioning";
        var workflowFile = _configuration["Provisioning:DestroyWorkflowFile"] ?? "destroy-tenant.yml";

        var body = new
        {
            @ref = "main",
            inputs = new
            {
                tenant_slug = tenantSlug,
                environment,
                confirm = tenantSlug
            }
        };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"repos/{repo}/actions/workflows/{workflowFile}/dispatches",
            content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("GitHub API error triggering destroy: {Status} {Body}", response.StatusCode, error);
            throw new HttpRequestException($"Failed to trigger destroy: {response.StatusCode}");
        }

        _logger.LogInformation("Triggered destroy for tenant {Slug}", tenantSlug);
    }

    /// <summary>
    /// Dispatches the migrate-and-grant-tenant workflow in TenantProvisioning. The workflow
    /// applies pending DataModel EF migrations AND ensures every service identity in its
    /// IDENTITIES map is granted on the tenant DB — both idempotent.
    ///
    /// The workflow updates the supplied <paramref name="jobId"/> row in TenantJobs at the
    /// end (success → Completed, failure → Error with run URL), so the admin UI's existing
    /// polling endpoint sees the transition naturally without any extra plumbing here.
    /// </summary>
    public async Task TriggerMigrateAndGrantAsync(string tenantSlug, Guid tenantId, Guid jobId)
    {
        var environment = _configuration["Provisioning:Environment"] ?? "testing";
        var repo = _configuration["Provisioning:Repo"] ?? "ProDataStack/ProDataStack.CDP.TenantProvisioning";
        var workflowFile = _configuration["Provisioning:MigrateWorkflowFile"] ?? "migrate-and-grant-tenant.yml";

        var body = new
        {
            @ref = "main",
            inputs = new
            {
                tenant_slug = tenantSlug,
                tenant_id = tenantId.ToString(),
                job_id = jobId.ToString(),
                environment
            }
        };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"repos/{repo}/actions/workflows/{workflowFile}/dispatches",
            content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("GitHub API error triggering migrate-and-grant: {Status} {Body}", response.StatusCode, error);
            throw new HttpRequestException($"Failed to trigger migrate-and-grant: {response.StatusCode}");
        }

        _logger.LogInformation("Triggered migrate-and-grant for tenant {Slug} (jobId={JobId})", tenantSlug, jobId);
    }
}
