namespace Dockmaster.Server.Api;

public sealed record RegistryDto(int Id, string Name, string Url, string Username, bool Insecure, bool HasPassword, string Aliases);
public sealed record SaveRegistryRequest(string Name, string Url, string Username, string? Password, bool Insecure, string? Aliases = null);
