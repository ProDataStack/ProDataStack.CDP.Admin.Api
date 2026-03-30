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
}
