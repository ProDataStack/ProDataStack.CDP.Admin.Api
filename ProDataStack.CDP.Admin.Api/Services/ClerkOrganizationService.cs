using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProDataStack.CDP.Admin.Api.Services;

/// <summary>
/// Wraps Clerk REST API for Organization operations.
/// The Clerk.BackendAPI NuGet package (v0.13.0) doesn't include Organization APIs,
/// so we call the REST API directly.
/// </summary>
public class ClerkOrganizationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ClerkOrganizationService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ClerkOrganizationService(HttpClient httpClient, ILogger<ClerkOrganizationService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ClerkOrg?> CreateOrganizationAsync(string name, string slug, string createdBy)
    {
        var body = new { name, created_by = createdBy };
        var response = await PostAsync<ClerkOrg>("organizations", body);
        _logger.LogInformation("Created Clerk Organization: {OrgId} ({Name})", response?.Id, name);
        return response;
    }

    public async Task<ClerkOrgInvitation?> InviteUserAsync(string orgId, string emailAddress, string role)
    {
        var body = new { email_address = emailAddress, role, inviter_user_id = (string?)null };
        return await PostAsync<ClerkOrgInvitation>($"organizations/{orgId}/invitations", body);
    }

    public async Task<List<ClerkOrgMember>> ListMembersAsync(string orgId)
    {
        var result = await GetAsync<ClerkMembershipList>($"organizations/{orgId}/memberships?limit=100");
        return result?.Data ?? [];
    }

    public async Task<List<ClerkUserOrgMembership>> ListUserOrganizationMembershipsAsync(string userId)
    {
        var result = await GetAsync<ClerkUserMembershipList>($"users/{userId}/organization_memberships?limit=100");
        return result?.Data ?? [];
    }

    public async Task RemoveMemberAsync(string orgId, string userId)
    {
        var response = await _httpClient.DeleteAsync($"organizations/{orgId}/memberships/{userId}");
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteOrganizationAsync(string orgId)
    {
        var response = await _httpClient.DeleteAsync($"organizations/{orgId}");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Find a Clerk Organization by exact name. Returns null if not found.
    /// </summary>
    public async Task<ClerkOrg?> FindOrganizationByNameAsync(string name)
    {
        var result = await GetAsync<ClerkOrgList>($"organizations?query={Uri.EscapeDataString(name)}&limit=10");
        return result?.Data?.FirstOrDefault(o =>
            string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<T?> PostAsync<T>(string path, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(path, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Clerk API error: {Status} {Body}", response.StatusCode, responseBody);
            throw new HttpRequestException($"Clerk API returned {response.StatusCode}: {responseBody}");
        }

        return JsonSerializer.Deserialize<T>(responseBody, JsonOptions);
    }

    private async Task<T?> GetAsync<T>(string path)
    {
        var response = await _httpClient.GetAsync(path);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Clerk API error: {Status} {Body}", response.StatusCode, responseBody);
            throw new HttpRequestException($"Clerk API returned {response.StatusCode}: {responseBody}");
        }

        return JsonSerializer.Deserialize<T>(responseBody, JsonOptions);
    }
}

public record ClerkOrg
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
}

public record ClerkOrgInvitation
{
    public string Id { get; init; } = "";
    public string EmailAddress { get; init; } = "";
    public string Status { get; init; } = "";
    public string Role { get; init; } = "";
}

public record ClerkOrgMember
{
    public string Id { get; init; } = "";
    public string Role { get; init; } = "";
    public ClerkPublicUserData? PublicUserData { get; init; }
    public long? CreatedAt { get; init; }
}

public record ClerkPublicUserData
{
    public string UserId { get; init; } = "";
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Identifier { get; init; }
}

public record ClerkMembershipList
{
    public List<ClerkOrgMember> Data { get; init; } = [];
    public long TotalCount { get; init; }
}

public record ClerkOrgList
{
    public List<ClerkOrg> Data { get; init; } = [];
    public long TotalCount { get; init; }
}

public record ClerkUserOrgMembership
{
    public string Id { get; init; } = "";
    public string Role { get; init; } = "";
    public ClerkOrg? Organization { get; init; }
}

public record ClerkUserMembershipList
{
    public List<ClerkUserOrgMembership> Data { get; init; } = [];
    public long TotalCount { get; init; }
}
