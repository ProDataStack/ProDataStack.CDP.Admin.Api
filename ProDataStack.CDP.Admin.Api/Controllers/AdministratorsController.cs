using Microsoft.AspNetCore.Mvc;
using ProDataStack.CDP.Admin.Api.Models;
using ProDataStack.CDP.Admin.Api.Services;

namespace ProDataStack.CDP.Admin.Api.Controllers;

/// <summary>
/// Manages platform administrators (ProDataStack Clerk Organisation members).
/// All endpoints require the caller to be a ProDataStack org member (verified via JWT org_slug).
/// Invite/remove operations require org:admin role.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class AdministratorsController : ControllerBase
{
    private const string PlatformOrgName = "ProDataStack";
    private const string RequiredEmailDomain = "@prodatastack.com";

    private readonly ClerkOrganizationService _clerkService;
    private readonly ILogger<AdministratorsController> _logger;

    // Cached org ID to avoid repeated lookups
    private static string? _cachedPlatformOrgId;
    private static readonly SemaphoreSlim OrgIdLock = new(1, 1);

    public AdministratorsController(
        ClerkOrganizationService clerkService,
        ILogger<AdministratorsController> logger)
    {
        _clerkService = clerkService;
        _logger = logger;
    }

    /// <summary>List all platform administrators (ProDataStack org members).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<TenantUserResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<TenantUserResponse>>> ListAdministrators()
    {
        var orgId = await ResolvePlatformOrgIdAsync(PlatformOrgName);
        if (orgId == null)
            return StatusCode(503, new { error = "ProDataStack organisation not found in identity provider. Has it been created?" });

        try
        {
            var members = await _clerkService.ListMembersAsync(orgId);
            var admins = members.Select(m => new TenantUserResponse
            {
                UserId = m.PublicUserData?.UserId ?? m.Id,
                Email = m.PublicUserData?.Identifier,
                FirstName = m.PublicUserData?.FirstName,
                LastName = m.PublicUserData?.LastName,
                Role = m.Role,
                JoinedAt = m.CreatedAt.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedAt.Value)
                    : null
            }).ToList();

            return Ok(admins);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list ProDataStack org members");
            return StatusCode(502, new { error = "Failed to retrieve administrators" });
        }
    }

    /// <summary>Invite a new platform administrator. Must be @prodatastack.com email. Requires org:admin role.</summary>
    [HttpPost("invite")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> InviteAdministrator([FromBody] InviteAdministratorRequest request)
    {
        if (!IsOrgAdmin())
            return Forbid();

        if (string.IsNullOrWhiteSpace(request.EmailAddress))
            return BadRequest(new { error = "Email address is required" });

        if (!request.EmailAddress.EndsWith(RequiredEmailDomain, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Email address must be {RequiredEmailDomain}" });

        var orgId = await ResolvePlatformOrgIdAsync(PlatformOrgName);
        if (orgId == null)
            return StatusCode(503, new { error = "ProDataStack organisation not found in identity provider" });

        try
        {
            var invitation = await _clerkService.InviteUserAsync(orgId, request.EmailAddress, "org:member");
            _logger.LogInformation("Invited administrator {Email}", request.EmailAddress);
            return Ok(new { message = "Invitation sent", email = request.EmailAddress, invitationId = invitation?.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invite administrator {Email}", request.EmailAddress);
            return StatusCode(502, new { error = "Failed to send invitation" });
        }
    }

    /// <summary>Remove a platform administrator from the ProDataStack org. Requires org:admin role.</summary>
    [HttpDelete("{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> RemoveAdministrator(string userId)
    {
        if (!IsOrgAdmin())
            return Forbid();

        var orgId = await ResolvePlatformOrgIdAsync(PlatformOrgName);
        if (orgId == null)
            return StatusCode(503, new { error = "ProDataStack organisation not found in identity provider" });

        try
        {
            await _clerkService.RemoveMemberAsync(orgId, userId);
            _logger.LogInformation("Removed administrator {UserId} from ProDataStack org", userId);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove administrator {UserId}", userId);
            return StatusCode(502, new { error = "Failed to remove administrator" });
        }
    }

    private bool IsOrgAdmin()
    {
        var orgRole = User.FindFirst("org_role")?.Value;
        return orgRole == "org:admin";
    }

    private async Task<string?> ResolvePlatformOrgIdAsync(string orgName)
    {
        if (_cachedPlatformOrgId != null)
            return _cachedPlatformOrgId;

        await OrgIdLock.WaitAsync();
        try
        {
            if (_cachedPlatformOrgId != null)
                return _cachedPlatformOrgId;

            var org = await _clerkService.FindOrganizationByNameAsync(orgName);
            if (org != null)
            {
                _cachedPlatformOrgId = org.Id;
                _logger.LogInformation("Resolved ProDataStack org ID: {OrgId}", org.Id);
            }

            return _cachedPlatformOrgId;
        }
        finally
        {
            OrgIdLock.Release();
        }
    }
}
