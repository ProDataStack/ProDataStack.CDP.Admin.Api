namespace ProDataStack.CDP.Admin.Api.Services;

/// <summary>
/// Provisions, migrates, and destroys tenant databases. Two implementations exist:
/// <see cref="ProvisioningService"/> dispatches GitHub Actions workflows (Production/Testing),
/// and <see cref="LocalProvisioningService"/> drives the local SQL Server directly (Development).
/// Selected at startup based on <c>ASPNETCORE_ENVIRONMENT</c>.
/// </summary>
public interface IProvisioningService
{
    /// <summary>
    /// Provisions a new tenant database for the catalog row identified by <paramref name="tenantId"/>.
    /// On success the catalog row is left in a state where the tenant can be used (cloud: row stays
    /// <c>Pending</c> and is flipped to <c>Ready</c> by the workflow; local: row is updated to
    /// <c>Ready</c> with the connection string before this method returns).
    /// </summary>
    /// <param name="tenantSlug">Sanitised slug (<c>[a-z0-9-]</c>) used to derive the database name.</param>
    /// <param name="tenantId">Catalog tenant ID (<c>Tenants.Id</c>) to associate the new database with.</param>
    /// <exception cref="HttpRequestException">Cloud only: thrown if the GitHub workflow dispatch fails.</exception>
    Task TriggerProvisioningAsync(string tenantSlug, Guid tenantId);

    /// <summary>
    /// Tears down a tenant's database. The caller is responsible for deleting the catalog row and
    /// the Clerk organisation; this method only handles the database side. Idempotent — completes
    /// successfully even if the database does not exist.
    /// </summary>
    /// <param name="tenantSlug">Sanitised slug used to derive the database name.</param>
    /// <exception cref="HttpRequestException">Cloud only: thrown if the GitHub workflow dispatch fails.</exception>
    Task TriggerDestroyAsync(string tenantSlug);

    /// <summary>
    /// Brings an existing tenant up to date with the current platform state by applying any pending
    /// <c>CdpDbContext</c> migrations. The cloud implementation also (re-)grants every service identity
    /// on the tenant DB; the local implementation skips grants since everything connects as <c>sa</c>.
    /// Both are idempotent. The supplied <paramref name="jobId"/> row in <c>TenantJobs</c> is updated
    /// to <c>Completed</c> on success or <c>Error</c> on failure, so the admin UI's existing polling
    /// sees the transition without any extra plumbing.
    /// </summary>
    /// <param name="tenantSlug">Sanitised slug — used by the cloud workflow; ignored locally (tenant is looked up by ID).</param>
    /// <param name="tenantId">Catalog tenant ID whose connection string is used to locate the database.</param>
    /// <param name="jobId">The <c>TenantJobs</c> row to update with the result.</param>
    /// <exception cref="HttpRequestException">Cloud only: thrown if the GitHub workflow dispatch fails.</exception>
    Task TriggerMigrateAndGrantAsync(string tenantSlug, Guid tenantId, Guid jobId);
}
