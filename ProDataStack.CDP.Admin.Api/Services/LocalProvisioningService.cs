using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ProDataStack.CDP.DataModel.Context;
using ProDataStack.CDP.TenantCatalog.Context;
using ProDataStack.CDP.TenantCatalog.Entities;

namespace ProDataStack.CDP.Admin.Api.Services;

/// <summary>
/// Development-only implementation of <see cref="IProvisioningService"/> that drives the
/// local SQL Server directly instead of dispatching cloud workflows: creates the per-tenant
/// database, applies <c>CdpDbContext</c> migrations, and updates the catalog row to Ready.
/// </summary>
public class LocalProvisioningService : IProvisioningService
{
    private readonly ILogger<LocalProvisioningService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IDbContextFactory<TenantCatalogDbContext> _catalogFactory;

    public LocalProvisioningService(
        ILogger<LocalProvisioningService> logger,
        IConfiguration configuration,
        IDbContextFactory<TenantCatalogDbContext> catalogFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _catalogFactory = catalogFactory;
    }

    /// <inheritdoc/>
    public async Task TriggerProvisioningAsync(string tenantSlug, Guid tenantId)
    {
        var (masterCs, tenantCs, dbName) = BuildConnectionStrings(tenantSlug);

        await using (var conn = new SqlConnection(masterCs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"IF DB_ID(@n) IS NULL CREATE DATABASE [{dbName}]";
            cmd.Parameters.AddWithValue("@n", dbName);
            await cmd.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<CdpDbContext>().UseSqlServer(tenantCs).Options;
        await using (var db = new CdpDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await using var catalog = await _catalogFactory.CreateDbContextAsync();
        var tenant = await catalog.Tenants.FindAsync(tenantId);
        if (tenant != null)
        {
            tenant.DatabaseServer = new SqlConnectionStringBuilder(tenantCs).DataSource;
            tenant.DatabaseName = dbName;
            tenant.ConnectionString = tenantCs;
            tenant.ProvisioningStatus = ProvisioningStatus.Ready;
            tenant.ProvisioningError = null;
            tenant.UpdatedAt = DateTimeOffset.UtcNow;
            await catalog.SaveChangesAsync();
        }

        _logger.LogInformation("Locally provisioned tenant {Slug} -> {Db}", tenantSlug, dbName);
    }

    /// <inheritdoc/>
    public async Task TriggerDestroyAsync(string tenantSlug)
    {
        var (masterCs, _, dbName) = BuildConnectionStrings(tenantSlug);
        await using var conn = new SqlConnection(masterCs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
IF DB_ID(@n) IS NOT NULL
BEGIN
    ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{dbName}];
END";
        cmd.Parameters.AddWithValue("@n", dbName);
        await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("Locally destroyed tenant DB {Db}", dbName);
    }

    /// <inheritdoc/>
    public async Task TriggerMigrateAndGrantAsync(string tenantSlug, Guid tenantId, Guid jobId)
    {
        await using var catalog = await _catalogFactory.CreateDbContextAsync();
        var tenant = await catalog.Tenants.FindAsync(tenantId);
        var job = await catalog.TenantJobs.FindAsync(jobId);
        if (job == null) return;

        try
        {
            if (string.IsNullOrEmpty(tenant?.ConnectionString))
                throw new InvalidOperationException("Tenant has no connection string");

            var options = new DbContextOptionsBuilder<CdpDbContext>().UseSqlServer(tenant.ConnectionString).Options;
            await using var db = new CdpDbContext(options);
            await db.Database.MigrateAsync();
            job.Status = TenantJobStatus.Completed;
        }
        catch (Exception ex)
        {
            job.Status = TenantJobStatus.Error;
            job.Error = ex.Message;
            _logger.LogError(ex, "Local migrate failed for tenant {TenantId}", tenantId);
        }

        job.CompletedAt = DateTimeOffset.UtcNow;
        await catalog.SaveChangesAsync();
    }

    // Slug is sanitised to [a-z0-9-] in TenantsController.GenerateSlug, so direct
    // interpolation into CREATE/DROP DATABASE is safe (DB names cannot be parameterised).
    private (string MasterCs, string TenantCs, string DbName) BuildConnectionStrings(string tenantSlug)
    {
        var catalogCs = _configuration.GetConnectionString("TenantCatalog")
            ?? throw new InvalidOperationException("TenantCatalog connection string is not configured");
        var builder = new SqlConnectionStringBuilder(catalogCs);
        var dbName = $"cdp-tenant-{tenantSlug}";
        builder.InitialCatalog = "master";
        var masterCs = builder.ConnectionString;
        builder.InitialCatalog = dbName;
        return (masterCs, builder.ConnectionString, dbName);
    }
}
