namespace ProDataStack.CDP.Admin.Api.Models;

public record CreateTenantRequest
{
    public required string Name { get; init; }
}

public record InviteUserRequest
{
    public required string EmailAddress { get; init; }
    public string Role { get; init; } = "org:member";
}

public record TenantResponse
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string ClerkOrganizationId { get; init; }
    public required string ProvisioningStatus { get; init; }
    public string? ProvisioningError { get; init; }
    public string? DatabaseServer { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record TenantUserResponse
{
    public required string UserId { get; init; }
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Role { get; init; }
    public DateTimeOffset? JoinedAt { get; init; }
}

public record InviteAdministratorRequest
{
    public required string EmailAddress { get; init; }
}
