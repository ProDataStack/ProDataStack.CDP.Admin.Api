using System.Text.RegularExpressions;
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

        var quotedDb = QuoteDbIdentifier(dbName);

        await using (var conn = new SqlConnection(masterCs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"IF DB_ID(@n) IS NULL CREATE DATABASE {quotedDb}";
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
        var quotedDb = QuoteDbIdentifier(dbName);

        await using var conn = new SqlConnection(masterCs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
IF DB_ID(@n) IS NOT NULL
BEGIN
    ALTER DATABASE {quotedDb} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {quotedDb};
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

    // DB names can't be parameterised in T-SQL, so identifiers must be interpolated.
    // Slugs reaching here are already sanitised by TenantsController.GenerateSlug, but
    // we re-validate locally so this file's DDL safety doesn't depend on a regex
    // defined in another class. Plus a defensive ]-double-up in case the contract
    // ever shifts.
    private static readonly Regex DbNamePattern = new(
        @"^cdp-tenant-[a-z0-9-]{1,50}$",
        RegexOptions.Compiled);

    private static string QuoteDbIdentifier(string dbName)
    {
        if (!DbNamePattern.IsMatch(dbName))
            throw new ArgumentException(
                $"Refusing to use '{dbName}' as a SQL identifier — must match {DbNamePattern}",
                nameof(dbName));

        return "[" + dbName.Replace("]", "]]") + "]";
    }
}
