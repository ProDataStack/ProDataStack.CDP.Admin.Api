# CDP Admin API

Platform administration service for the Customer Data Platform. Manages tenants, users, and platform-level operations.

## Endpoints

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/v1/health-check` | Anonymous | Health check |
| GET | `/api/v1/tenants` | Authenticated | List all tenants from catalog |
| GET | `/api/v1/tenants/{id}` | Authenticated | Single tenant detail |
| POST | `/api/v1/tenants` | Authenticated | Create tenant (Clerk org + catalog + trigger provisioning) |
| POST | `/api/v1/tenants/{id}/invite` | Authenticated | Invite user via Clerk REST API |
| GET | `/api/v1/tenants/{id}/users` | Authenticated | List org members via Clerk REST API |

## Auth Model

See `CDP/iterations/1/CDP-ADMIN-AUTH.md` in the planning repo for the full auth specification.

The Admin API uses Clerk JWT auth with `org_id` claims. Platform administrators are identified by their Clerk `org:admin` role. The Admin app is a separate Clerk application from the tenant-facing CDP app.

## Tenant Provisioning Flow

1. POST `/api/v1/tenants` with org name
2. Admin API creates Clerk Organization via Clerk REST API
3. Creates catalog entry with status `Provisioning`
4. Triggers `ProDataStack.CDP.TenantProvisioning` GitHub Actions workflow
5. Pipeline creates dedicated SQL Server + DB, grants managed identity access, runs migrations
6. Tenant status set to `Ready`

## Infrastructure

- **Resource group**: `cdp-admin-api`
- **Key Vaults**: `cdp-admin-api-test` / `cdp-admin-api-prod`
- **Managed identity client IDs**: Testing=`c5da567b-e83e-4739-96ad-4c4d0e3a7472`, Production=`1a5ece5d-bc7c-416f-b9ba-1168bf5dd698`
- **Docker image**: `cdp.admin.api`
- **Namespaces**: `cdp-testing` / `cdp-production`
- **Hostname**: `cdp-admin-api.testing.pdsnextgen.com` / `cdp-admin-api.prodatastack.com`

## Dependencies

- `ProDataStack.Chassis` — shared service chassis (NuGet)
- `ProDataStack.CDP.DataModel` — shared data model (NuGet). Admin API runs EF migrations against tenant DBs.
- `ProDataStack.CDP.TenantCatalog` — tenant catalog DbContext (NuGet)

When `DataModel` changes, Admin API should be redeployed **first** (it runs migrations), before other services.

## Iteration 2 migrations (shipped)

The I2 DataModel NuGet (1.0.x) ships:
- **Segmentation tables** — `Segment`, `SegmentField`, `ExportLog` (migration `AddSegmentationEntities`)
- **Connector runtime tables** — `ConnectorConfig`, `ConnectorSyncJob`, `ConnectorSyncStaging`, `ConnectorSyncError` (migration `AddConnectorRuntime`). State store for `ProDataStack.CDP.Connectors.Api`. Required indexes on `ConnectorSyncJob (Status, ScheduledFor)` (worker claim) and `ConnectorSyncJob (Status, HeartbeatAt)` (reaper) are in the migration.

The **Migrate** button in cdp-admin (which dispatches `migrate-and-grant-tenant.yml` in TenantProvisioning) now applies pending migrations AND grants every service identity in one click — see `CDP/CLAUDE.md` notes #8 and #10. Admin API's `MigrateTenant` endpoint dispatches that workflow rather than running EF inline (privilege boundary).

Full spec: `CDP/iterations/2/ITERATION-2-TICKETS.md` § Epic 1 and § DataModel NuGet Changes Required. Decision rationale: `CDP/CLAUDE.md` note #11.
