using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProDataStack.CDP.Admin.Api.Models;
using ProDataStack.CDP.Admin.Api.Services;
using ProDataStack.CDP.TenantCatalog.Context;
using ProDataStack.CDP.TenantCatalog.Entities;

namespace ProDataStack.CDP.Admin.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class TenantsController : ControllerBase
{
    private readonly IDbContextFactory<TenantCatalogDbContext> _catalogFactory;
    private readonly ClerkOrganizationService _clerkService;
    private readonly ProvisioningService _provisioningService;
    private readonly ILogger<TenantsController> _logger;

    public TenantsController(
        IDbContextFactory<TenantCatalogDbContext> catalogFactory,
        ClerkOrganizationService clerkService,
        ProvisioningService provisioningService,
        ILogger<TenantsController> logger)
    {
        _catalogFactory = catalogFactory;
        _clerkService = clerkService;
        _provisioningService = provisioningService;
        _logger = logger;
    }

    [HttpGet("/api/v1/health-check")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult HealthCheck()
    {
        return Ok(new { status = "ok", service = "CDP Admin API" });
    }

    /// <summary>List all tenants with provisioning status.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<TenantResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<TenantResponse>>> ListTenants()
    {
        await using var db = await _catalogFactory.CreateDbContextAsync();
        var tenants = await db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => MapToResponse(t))
            .ToListAsync();

        return Ok(tenants);
    }

    /// <summary>
    /// List the tenants the current user has access to.
    /// Exclude orgs that are not in the tenant catalog i.e. ProDataStack which is used for platform admin only.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(List<TenantResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<TenantResponse>>> ListMyTenants()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        List<ClerkUserOrgMembership> memberships;
        try
        {
            memberships = await _clerkService.ListUserOrganizationMembershipsAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list Clerk org memberships for user {UserId}", userId);
            return StatusCode(502, new { error = "Failed to retrieve user organisations" });
        }

        var orgIds = memberships
            .Select(m => m.Organization?.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();

        if (orgIds.Count == 0)
            return Ok(new List<TenantResponse>());

        await using var db = await _catalogFactory.CreateDbContextAsync();
        var tenants = await db.Tenants
            .AsNoTracking()
            .Where(t => orgIds.Contains(t.ClerkOrganizationId))
            .OrderBy(t => t.Name)
            .Select(t => MapToResponse(t))
            .ToListAsync();

        return Ok(tenants);
    }

    /// <summary>Get a single tenant by ID.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantResponse>> GetTenant(Guid id)
    {
        await using var db = await _catalogFactory.CreateDbContextAsync();
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
            return NotFound();

        return Ok(MapToResponse(tenant));
    }

    /// <summary>
    /// Create a new tenant organisation.
    /// Creates Clerk Organization, inserts catalog record, triggers provisioning pipeline.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TenantResponse>> CreateTenant([FromBody] CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required" });

        var slug = GenerateSlug(request.Name);

        await using var db = await _catalogFactory.CreateDbContextAsync();

        // Check name uniqueness
        if (await db.Tenants.AnyAsync(t => t.Name == request.Name))
            return Conflict(new { error = "An organisation with this name already exists" });

        // Get the authenticated user's ID for CreatedBy
        // .NET maps "sub" to the long nameidentifier URI by default
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? "system";

        // 1. Create Clerk Organization
        ClerkOrg? clerkOrg;
        try
        {
            clerkOrg = await _clerkService.CreateOrganizationAsync(request.Name, slug, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Clerk Organization for {Name}", request.Name);
            return StatusCode(502, new { error = "Failed to create organisation in identity provider" });
        }

        // 2. Insert tenant record in catalog
        var tenant = new Tenant
        {
            ClerkOrganizationId = clerkOrg!.Id,
            Name = request.Name,
            DatabaseName = "cdp",
            DatabaseServer = $"cdp-{slug}.database.windows.net",
            ProvisioningStatus = ProvisioningStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedBy = userId
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // 3. Trigger provisioning pipeline
        try
        {
            await _provisioningService.TriggerProvisioningAsync(slug, tenant.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger provisioning for {Name}", request.Name);
            tenant.ProvisioningStatus = ProvisioningStatus.Error;
            tenant.ProvisioningError = "Failed to trigger provisioning pipeline";
            tenant.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        _logger.LogInformation("Created tenant {Name} ({Id}) with Clerk org {OrgId}", request.Name, tenant.Id, clerkOrg.Id);

        return AcceptedAtAction(nameof(GetTenant), new { id = tenant.Id }, MapToResponse(tenant));
    }

    /// <summary>Invite a user to a tenant organisation via Clerk.</summary>
    [HttpPost("{id:guid}/invite")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> InviteUser(Guid id, [FromBody] InviteUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EmailAddress))
            return BadRequest(new { error = "Email address is required" });

        await using var db = await _catalogFactory.CreateDbContextAsync();
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
            return NotFound();

        if (tenant.ProvisioningStatus != ProvisioningStatus.Ready)
            return BadRequest(new { error = "Organisation is not ready. Current status: " + tenant.ProvisioningStatus });

        try
        {
            var invitation = await _clerkService.InviteUserAsync(
                tenant.ClerkOrganizationId,
                request.EmailAddress,
                request.Role);

            return Ok(new { message = "Invitation sent", email = request.EmailAddress, invitationId = invitation?.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invite {Email} to tenant {Id}", request.EmailAddress, id);
            return StatusCode(502, new { error = "Failed to send invitation" });
        }
    }

    /// <summary>List members of a tenant organisation from Clerk.</summary>
    [HttpGet("{id:guid}/users")]
    [ProducesResponseType(typeof(List<TenantUserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<TenantUserResponse>>> ListUsers(Guid id)
    {
        await using var db = await _catalogFactory.CreateDbContextAsync();
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
            return NotFound();

        try
        {
            var members = await _clerkService.ListMembersAsync(tenant.ClerkOrganizationId);
            var users = members.Select(m => new TenantUserResponse
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

            return Ok(users);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list members for tenant {Id}", id);
            return StatusCode(502, new { error = "Failed to retrieve organisation members" });
        }
    }

    /// <summary>
    /// Delete a tenant: triggers destroy pipeline, deletes Clerk org, removes catalog record.
    /// Requires confirmName to match the tenant name. Returns a job ID for polling.
    /// Job survives tenant deletion (FK SET NULL).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteTenant(Guid id, [FromQuery] string? confirmName)
    {
        await using var db = await _catalogFactory.CreateDbContextAsync();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(confirmName) || confirmName != tenant.Name)
            return BadRequest(new { error = "Confirmation name does not match the organisation name" });

        var slug = GenerateSlug(tenant.Name);
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        var job = new TenantJob
        {
            Id = Guid.NewGuid(),
            TenantId = id,
            JobType = TenantJobType.Destroy,
            Status = TenantJobStatus.InProgress,
            Description = $"Delete organisation '{tenant.Name}'",
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.TenantJobs.Add(job);
        await db.SaveChangesAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                // 1. Trigger destroy pipeline
                if (tenant.ProvisioningStatus == ProvisioningStatus.Ready ||
                    tenant.ProvisioningStatus == ProvisioningStatus.Error)
                {
                    try
                    {
                        await _provisioningService.TriggerDestroyAsync(slug);
                        _logger.LogInformation("Triggered destroy pipeline for {Slug}", slug);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to trigger destroy pipeline for {Name}", tenant.Name);
                    }
                }

                // 2. Delete Clerk Organization
                try
                {
                    await _clerkService.DeleteOrganizationAsync(tenant.ClerkOrganizationId);
                    _logger.LogInformation("Deleted Clerk org {OrgId}", tenant.ClerkOrganizationId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete Clerk org {OrgId}", tenant.ClerkOrganizationId);
                }

                // 3. Remove from catalog (job survives — FK SET NULL)
                await using var updateDb = await _catalogFactory.CreateDbContextAsync();
                var t = await updateDb.Tenants.FindAsync(id);
                if (t != null)
                {
                    updateDb.Tenants.Remove(t);
                    await updateDb.SaveChangesAsync();
                }

                // 4. Mark job completed
                var j = await updateDb.TenantJobs.FindAsync(job.Id);
                if (j != null)
                {
                    j.Status = TenantJobStatus.Completed;
                    j.CompletedAt = DateTimeOffset.UtcNow;
                    await updateDb.SaveChangesAsync();
                }

                _logger.LogInformation("Deleted tenant {Name} ({Id})", tenant.Name, id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete tenant {Name} ({Id})", tenant.Name, id);
                try
                {
                    await using var errDb = await _catalogFactory.CreateDbContextAsync();
                    var j = await errDb.TenantJobs.FindAsync(job.Id);
                    if (j != null)
                    {
                        j.Status = TenantJobStatus.Error;
                        j.Error = ex.Message;
                        j.CompletedAt = DateTimeOffset.UtcNow;
                        await errDb.SaveChangesAsync();
                    }
                }
                catch { }
            }
        });

        return Accepted(new { jobId = job.Id, status = job.Status });
    }

    /// <summary>Get a job by ID (not scoped to tenant — survives tenant deletion).</summary>
    [HttpGet("/api/v1/jobs/{jobId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetJobById(Guid jobId)
    {
        await using var db = await _catalogFactory.CreateDbContextAsync();
        var job = await db.TenantJobs
            .Where(j => j.Id == jobId)
            .Select(j => new { j.Id, j.JobType, j.Status, j.Description, j.Error, j.CreatedBy, j.CreatedAt, j.CompletedAt })
            .FirstOrDefaultAsync();

        if (job == null) return NotFound();
        return Ok(job);
    }

    /// <summary>
    /// Bring a tenant up to date with the current platform state: applies pending
    /// DataModel EF migrations AND (re-)grants every service identity on the tenant DB.
    /// Both are idempotent. Click this after deploying a new service or a DataModel bump.
    ///
    /// The actual work runs in the migrate-and-grant-tenant.yml workflow in the
    /// TenantProvisioning repo. This endpoint creates a TenantJob row (Status=InProgress),
    /// dispatches the workflow with the job ID, and returns 202 immediately. The workflow
    /// updates the same job row to Completed/Error on its final step, so the admin UI's
    /// existing TenantJob polling sees the transition naturally.
    ///
    /// Why a workflow and not inline EF: the workflow runs as the CDP deployment SP,
    /// which is in the SQL AAD admin group and has the rights to CREATE USER for external
    /// identities. The Admin API pod identity does not. Routing through the workflow lets
    /// us do migrate + grant in one operation without elevating Admin API's DB privileges.
    /// </summary>
    [HttpPost("{id:guid}/migrate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> MigrateTenant(Guid id)
    {
        await using var db = await _catalogFactory.CreateDbContextAsync();
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
            return NotFound();

        if (tenant.ProvisioningStatus != ProvisioningStatus.Ready)
            return BadRequest(new { error = $"Tenant is not ready. Status: {tenant.ProvisioningStatus}" });

        if (string.IsNullOrEmpty(tenant.ConnectionString))
            return BadRequest(new { error = "Tenant has no connection string" });

        var slug = GenerateSlug(tenant.Name);

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        var job = new TenantJob
        {
            Id = Guid.NewGuid(),
            TenantId = id,
            JobType = TenantJobType.Migration,
            Status = TenantJobStatus.InProgress,
            Description = "Apply migrations + grant service identities",
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.TenantJobs.Add(job);
        await db.SaveChangesAsync();

        try
        {
            await _provisioningService.TriggerMigrateAndGrantAsync(slug, id, job.Id);
            _logger.LogInformation("Triggered migrate-and-grant for tenant {Name} ({Id}), job {JobId}", tenant.Name, id, job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger migrate-and-grant workflow for {Name} ({Id})", tenant.Name, id);

            // Mark job as error so the UI doesn't hang in InProgress
            try
            {
                await using var updateDb = await _catalogFactory.CreateDbContextAsync();
                var j = await updateDb.TenantJobs.FindAsync(job.Id);
                if (j != null)
                {
                    j.Status = TenantJobStatus.Error;
                    j.Error = "Failed to dispatch workflow: " + ex.Message;
                    j.CompletedAt = DateTimeOffset.UtcNow;
                    await updateDb.SaveChangesAsync();
                }
            }
            catch (Exception updateEx)
            {
                _logger.LogError(updateEx, "Failed to update job status for {JobId}", job.Id);
            }

            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Failed to dispatch migrate-and-grant workflow", details = ex.Message });
        }

        return Accepted(new { jobId = job.Id, status = job.Status });
    }

    /// <summary>List jobs for a tenant, most recent first.</summary>
    [HttpGet("{id:guid}/jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ListJobs(Guid id)
    {
        await using var db = await _catalogFactory.CreateDbContextAsync();
        var jobs = await db.TenantJobs
            .Where(j => j.TenantId == id)
            .OrderByDescending(j => j.CreatedAt)
            .Take(50)
            .Select(j => new
            {
                j.Id,
                j.JobType,
                j.Status,
                j.Description,
                j.Error,
                j.CreatedBy,
                j.CreatedAt,
                j.CompletedAt
            })
            .ToListAsync();

        return Ok(jobs);
    }

    /// <summary>Get a single job by ID.</summary>
    [HttpGet("{id:guid}/jobs/{jobId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetJob(Guid id, Guid jobId)
    {
        await using var db = await _catalogFactory.CreateDbContextAsync();
        var job = await db.TenantJobs
            .Where(j => j.TenantId == id && j.Id == jobId)
            .Select(j => new
            {
                j.Id,
                j.JobType,
                j.Status,
                j.Description,
                j.Error,
                j.CreatedBy,
                j.CreatedAt,
                j.CompletedAt
            })
            .FirstOrDefaultAsync();

        if (job == null)
            return NotFound();

        return Ok(job);
    }

    private static TenantResponse MapToResponse(Tenant t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        ClerkOrganizationId = t.ClerkOrganizationId,
        ProvisioningStatus = t.ProvisioningStatus.ToString(),
        ProvisioningError = t.ProvisioningError,
        DatabaseServer = t.DatabaseServer,
        CreatedAt = t.CreatedAt
    };

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
        slug = Regex.Replace(slug, @"\s+", "-");
        slug = slug.Trim('-');

        // Max 20 chars for SQL Server name constraint (cdp- prefix + slug)
        if (slug.Length > 20)
            slug = slug[..20].TrimEnd('-');

        return slug;
    }
}
