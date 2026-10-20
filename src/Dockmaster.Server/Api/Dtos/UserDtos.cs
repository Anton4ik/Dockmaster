namespace Dockmaster.Server.Api;

/// <param name="HasPassword">someone can sign in: false only on the first run, which makes the first admin</param>
public sealed record MeDto(bool Authenticated, bool HasPassword, MeUserDto? User = null);
/// <param name="Everywhere">permissions on every server</param>
/// <param name="Servers">server id → permissions on it (on top of <paramref name="Everywhere"/>)</param>
public sealed record MeUserDto(int Id, string Name, bool IsAdmin, string[] Everywhere, Dictionary<int, string[]> Servers);
/// <param name="Name">null = "admin", the one user there was before users existed</param>
public sealed record LoginRequest(string? Name, string Password);
/// <param name="Code">the setup code the server printed to its log at start</param>
public sealed record SetupRequest(string? Name, string Password, string? Code);
public sealed record PasswordRequest(string? Current, string Password);

/// <param name="AgentId">0 = every server, also ones added later</param>
public sealed record GrantDto(int AgentId, int RoleId);
public sealed record UserDto(int Id, string Name, bool IsAdmin, bool Disabled, GrantDto[] Grants, DateTime CreatedAt, DateTime? LastLoginAt);
/// <param name="Password">a new user's password; for an existing one, null or empty keeps it</param>
public sealed record SaveUserRequest(string? Name, string? Password, bool IsAdmin, bool Disabled, GrantDto[]? Grants);
/// <param name="Users">the names of the users who have it somewhere</param>
public sealed record RoleDto(int Id, string Name, string Description, string[] Permissions, string[] Users);
public sealed record SaveRoleRequest(string? Name, string? Description, string[]? Permissions);
public sealed record PermissionDto(string Key, string Label, string Description);
