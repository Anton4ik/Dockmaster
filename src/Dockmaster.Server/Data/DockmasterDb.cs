using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Data;

public sealed class DockmasterDb(DbContextOptions<DockmasterDb> options) : DbContext(options)
{
    public DbSet<AgentEntity> Agents => Set<AgentEntity>();
    public DbSet<RegistryEntity> Registries => Set<RegistryEntity>();
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<DeploymentEntity> Deployments => Set<DeploymentEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<ProjectSecretEntity> ProjectSecrets => Set<ProjectSecretEntity>();
    public DbSet<MeshNetworkEntity> MeshNetworks => Set<MeshNetworkEntity>();
    public DbSet<MeshNetworkMemberEntity> MeshNetworkMembers => Set<MeshNetworkMemberEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<ProjectFileEntity> ProjectFiles => Set<ProjectFileEntity>();
    public DbSet<JobRunEntity> JobRuns => Set<JobRunEntity>();
    public DbSet<TemplateEntity> Templates => Set<TemplateEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RoleEntity> Roles => Set<RoleEntity>();
    public DbSet<UserGrantEntity> UserGrants => Set<UserGrantEntity>();

    /// <summary>A project by its compose name on one agent (names are unique per agent).</summary>
    public Task<ProjectEntity?> ProjectByNameAsync(int agentId, string name, CancellationToken ct) =>
        Projects.FirstOrDefaultAsync(p => p.AgentId == agentId && p.Name == name, ct);

    /// <summary>Server id → name, the two columns only (a project row carries its whole compose file: not loaded for a name).</summary>
    public Task<Dictionary<int, string>> AgentNamesAsync(CancellationToken ct = default) =>
        Agents.Select(a => new { a.Id, a.Name }).ToDictionaryAsync(a => a.Id, a => a.Name, ct);

    /// <summary>Project id → name, the two columns only.</summary>
    public Task<Dictionary<int, string>> ProjectNamesAsync(CancellationToken ct = default) =>
        Projects.Select(p => new { p.Id, p.Name }).ToDictionaryAsync(p => p.Id, p => p.Name, ct);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AgentEntity>().HasIndex(a => a.Name).IsUnique();
        b.Entity<AgentEntity>().HasIndex(a => a.TokenHash).IsUnique();
        // compose project names are unique per host, not globally (two hosts may both run "database")
        b.Entity<ProjectEntity>().HasIndex(p => new { p.AgentId, p.Name }).IsUnique();
        // read from and written to AutoscaleExtra
        b.Entity<ProjectEntity>().Ignore(p => p.Extras);
        b.Entity<ProjectEntity>().Ignore(p => p.DeployedFingerprints);
        b.Entity<RegistryEntity>().HasIndex(r => r.Url).IsUnique();
        b.Entity<SettingEntity>().HasKey(s => s.Key);
        b.Entity<DeploymentEntity>().HasIndex(d => d.ProjectId);
        b.Entity<ProjectSecretEntity>().HasIndex(s => new { s.ProjectId, s.Name }).IsUnique();
        b.Entity<ProjectFileEntity>().HasIndex(f => new { f.ProjectId, f.Name }).IsUnique();
        b.Entity<MeshNetworkEntity>().HasIndex(n => n.Name).IsUnique();
        b.Entity<MeshNetworkMemberEntity>().HasIndex(m => new { m.NetworkId, m.AgentId }).IsUnique();
        b.Entity<EventEntity>().HasIndex(e => e.At);
        b.Entity<EventEntity>().HasIndex(e => e.ProjectId);
        b.Entity<JobRunEntity>().HasIndex(r => new { r.ProjectId, r.Service, r.StartedAt });
        b.Entity<TemplateEntity>().HasIndex(t => t.Name).IsUnique();
        b.Entity<ApiKeyEntity>().HasIndex(k => k.KeyHash).IsUnique();
        b.Entity<ProjectEntity>().HasIndex(p => p.TemplateId);
        // "Admin" and "admin" are one person, "Viewer" and "viewer" one role
        b.Entity<UserEntity>().Property(u => u.Name).UseCollation("NOCASE");
        b.Entity<UserEntity>().HasIndex(u => u.Name).IsUnique();
        b.Entity<RoleEntity>().Property(r => r.Name).UseCollation("NOCASE");
        b.Entity<RoleEntity>().HasIndex(r => r.Name).IsUnique();
        b.Entity<UserGrantEntity>().HasIndex(g => new { g.UserId, g.AgentId }).IsUnique();
    }

    /// <summary>
    /// EnsureCreated only builds a brand-new database; what was added to the model later is added here, so existing
    /// installations keep working without migrations. Every step is a no-op on a database that already has it.
    /// </summary>
    public async Task UpgradeSchemaAsync()
    {
        // tables, by feature
        await Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ProjectSecrets" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ProjectSecrets" PRIMARY KEY AUTOINCREMENT,
                "ProjectId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "ValueProtected" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ProjectSecrets_ProjectId_Name" ON "ProjectSecrets" ("ProjectId", "Name");

            CREATE TABLE IF NOT EXISTS "MeshNetworks" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_MeshNetworks" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "HubAgentId" INTEGER NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_MeshNetworks_Name" ON "MeshNetworks" ("Name");
            CREATE TABLE IF NOT EXISTS "MeshNetworkMembers" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_MeshNetworkMembers" PRIMARY KEY AUTOINCREMENT,
                "NetworkId" INTEGER NOT NULL,
                "AgentId" INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_MeshNetworkMembers_NetworkId_AgentId" ON "MeshNetworkMembers" ("NetworkId", "AgentId");

            CREATE TABLE IF NOT EXISTS "Events" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Events" PRIMARY KEY AUTOINCREMENT,
                "At" TEXT NOT NULL,
                "Level" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "ProjectId" INTEGER NULL,
                "AgentId" INTEGER NULL,
                "Service" TEXT NULL,
                "Message" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_Events_At" ON "Events" ("At");
            CREATE INDEX IF NOT EXISTS "IX_Events_ProjectId" ON "Events" ("ProjectId");

            CREATE TABLE IF NOT EXISTS "ProjectFiles" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ProjectFiles" PRIMARY KEY AUTOINCREMENT,
                "ProjectId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "Content" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ProjectFiles_ProjectId_Name" ON "ProjectFiles" ("ProjectId", "Name");

            CREATE TABLE IF NOT EXISTS "JobRuns" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_JobRuns" PRIMARY KEY AUTOINCREMENT,
                "ProjectId" INTEGER NOT NULL,
                "Service" TEXT NOT NULL,
                "AgentId" INTEGER NOT NULL,
                "ContainerId" TEXT NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "FinishedAt" TEXT NULL,
                "ExitCode" INTEGER NULL,
                "Ok" INTEGER NULL,
                "Trigger" TEXT NOT NULL,
                "Note" TEXT NOT NULL,
                "LogTail" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_JobRuns_ProjectId_Service_StartedAt" ON "JobRuns" ("ProjectId", "Service", "StartedAt");

            CREATE TABLE IF NOT EXISTS "Templates" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Templates" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "ComposeYaml" TEXT NOT NULL,
                "EnvFile" TEXT NOT NULL,
                "Variables" TEXT NOT NULL,
                "AgentIds" TEXT NOT NULL,
                "MaxInstances" INTEGER NOT NULL,
                "HealthTimeoutSeconds" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Templates_Name" ON "Templates" ("Name");

            CREATE TABLE IF NOT EXISTS "ApiKeys" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ApiKeys" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "KeyHash" TEXT NOT NULL,
                "Hint" TEXT NOT NULL,
                "TemplateIds" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "LastUsedAt" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ApiKeys_KeyHash" ON "ApiKeys" ("KeyHash");

            CREATE TABLE IF NOT EXISTS "Users" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT COLLATE NOCASE NOT NULL,
                "PasswordHash" TEXT NOT NULL,
                "IsAdmin" INTEGER NOT NULL,
                "Disabled" INTEGER NOT NULL,
                "SessionStamp" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "LastLoginAt" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Name" ON "Users" ("Name");
            CREATE TABLE IF NOT EXISTS "Roles" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Roles" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT COLLATE NOCASE NOT NULL,
                "Description" TEXT NOT NULL,
                "Permissions" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Roles_Name" ON "Roles" ("Name");
            CREATE TABLE IF NOT EXISTS "UserGrants" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UserGrants" PRIMARY KEY AUTOINCREMENT,
                "UserId" INTEGER NOT NULL,
                "AgentId" INTEGER NOT NULL,
                "RoleId" INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserGrants_UserId_AgentId" ON "UserGrants" ("UserId", "AgentId");
            """);

        // columns, by table
        await AddColumnsAsync("Agents",
            // mesh (WireGuard between agent hosts)
            ("PeerIp", "TEXT NULL"),
            ("MeshIp", "TEXT NULL"),
            ("MeshPublicKey", "TEXT NULL"),
            ("MeshPublic", "INTEGER NULL"),
            ("MeshEndpoint", "TEXT NULL"),
            ("MeshPort", "INTEGER NULL"),
            ("MeshJoinedAt", "TEXT NULL"),
            ("MeshHostIps", "TEXT NULL"),
            ("MeshPathMtu", "INTEGER NULL"));
        await AddColumnsAsync("Projects", ("DeployedAgentIds", "TEXT NOT NULL DEFAULT ''"), ("AutoscaleExtra", "TEXT NOT NULL DEFAULT ''"), ("DeployedServices", "TEXT NOT NULL DEFAULT ''"),
            ("HookProtected", "TEXT NOT NULL DEFAULT ''"),
            // instances of a template, created through an API key
            ("TemplateId", "INTEGER NULL"), ("InstanceVariables", "TEXT NOT NULL DEFAULT ''"));
        // what each deploy put out, to see what changed since and to go back to it
        await AddColumnsAsync("Deployments", ("ComposeYaml", "TEXT NOT NULL DEFAULT ''"), ("EnvFile", "TEXT NOT NULL DEFAULT ''"), ("Source", "TEXT NOT NULL DEFAULT ''"));
        await AddColumnsAsync("Registries", ("Aliases", "TEXT NOT NULL DEFAULT ''"));
        await AddColumnsAsync("MeshNetworks", ("Trusted", "INTEGER NOT NULL DEFAULT 0"));
        // events from before the column count as sent: a Seq set later does not get them all at once
        await AddColumnsAsync("Events", ("SentToSeq", "INTEGER NOT NULL DEFAULT 1"), ("Container", "TEXT NULL"), ("Details", "TEXT NULL"), ("User", "TEXT NULL"));

        // indexes: project names became unique per server instead of globally
        await Database.ExecuteSqlRawAsync("""
            DROP INDEX IF EXISTS "IX_Projects_Name";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Projects_AgentId_Name" ON "Projects" ("AgentId", "Name");
            CREATE INDEX IF NOT EXISTS "IX_Projects_TemplateId" ON "Projects" ("TemplateId");
            """);

        // one-off data migrations, each guarded by a marker in Settings
        // servers that joined the mesh before networks existed go into a "default" network with the old global hub
        await Database.ExecuteSqlRawAsync($"""
            INSERT INTO "MeshNetworks" ("Name", "HubAgentId", "CreatedAt")
                SELECT 'default', (SELECT CAST("Value" AS INTEGER) FROM "Settings" WHERE "Key" = '{SettingsService.LegacyMeshHubKey}' AND "Value" <> ''), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM "Settings" WHERE "Key" = '{SettingsService.MeshNetworksMarkerKey}')
                  AND EXISTS (SELECT 1 FROM "Agents" WHERE "MeshIp" IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM "MeshNetworks" WHERE "Name" = 'default');
            INSERT OR IGNORE INTO "MeshNetworkMembers" ("NetworkId", "AgentId")
                SELECT (SELECT "Id" FROM "MeshNetworks" WHERE "Name" = 'default'), "Id" FROM "Agents"
                WHERE "MeshIp" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "Settings" WHERE "Key" = '{SettingsService.MeshNetworksMarkerKey}')
                  AND EXISTS (SELECT 1 FROM "MeshNetworks" WHERE "Name" = 'default');
            INSERT OR IGNORE INTO "Settings" ("Key", "Value") VALUES ('{SettingsService.MeshNetworksMarkerKey}', '1');
            """);

        // the one admin password from before users existed becomes the user "admin" (the setting stays: a downgraded
        // server must not offer the first-run setup to whoever comes first)
        await Database.ExecuteSqlRawAsync($"""
            INSERT INTO "Users" ("Name", "PasswordHash", "IsAdmin", "Disabled", "SessionStamp", "CreatedAt")
                SELECT 'admin', "Value", 1, 0, lower(hex(randomblob(16))), datetime('now') FROM "Settings"
                WHERE "Key" = '{SettingsService.AdminPasswordKey}' AND "Value" <> '' AND NOT EXISTS (SELECT 1 FROM "Users");
            """);
        // the roles a new installation starts with; changed or deleted ones are not brought back
        if (!await Settings.AnyAsync(s => s.Key == Access.DefaultRoles.SeededMarkerKey))
        {
            foreach (var r in Access.DefaultRoles.All.Where(r => !Roles.Any(x => x.Name == r.Name)))
                Roles.Add(new RoleEntity { Name = r.Name, Description = r.Description, Permissions = string.Join(",", r.Permissions) });
            Settings.Add(new SettingEntity { Key = Access.DefaultRoles.SeededMarkerKey, Value = "1" });
            await SaveChangesAsync();
        }
    }

    /// <summary>SQLite has no ADD COLUMN IF NOT EXISTS: adds the columns the table does not have yet.</summary>
    private async Task AddColumnsAsync(string table, params (string Name, string Definition)[] columns)
    {
        var existing = (await Database.SqlQuery<string>($"SELECT \"name\" AS \"Value\" FROM pragma_table_info({table})").ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, definition) in columns.Where(c => !existing.Contains(c.Name)))
        {
            // names and definitions are the constants above, never input
#pragma warning disable EF1002
            try { await Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{name}\" {definition}"); }
#pragma warning restore EF1002
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column")) { } // another instance added it meanwhile
        }
    }
}

public sealed class AgentEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
    public string? Hostname { get; set; }
    public string? Os { get; set; }
    public string? Arch { get; set; }
    public string? DockerVersion { get; set; }
    public int Cpus { get; set; }
    public long MemoryTotal { get; set; }
    public string? AgentIp { get; set; }
    public string? AgentVersion { get; set; }

    // --- mesh (WireGuard between agent hosts) ---
    /// <summary>Address the agent's connection came from: the host's public address, or its NAT's.</summary>
    public string? PeerIp { get; set; }
    /// <summary>Address in the mesh subnet, e.g. 10.77.0.3; null = not joined.</summary>
    public string? MeshIp { get; set; }
    public string? MeshPublicKey { get; set; }
    /// <summary>Reachable from the internet on the mesh port: null = decide from the addresses, true/false = the user said so.</summary>
    public bool? MeshPublic { get; set; }
    /// <summary>host[:port] other hosts connect to, when the detected address is wrong.</summary>
    public string? MeshEndpoint { get; set; }
    /// <summary>UDP port dm0 listens on (the default port may be taken by another WireGuard on that host).</summary>
    public int? MeshPort { get; set; }
    public DateTime? MeshJoinedAt { get; set; }
    /// <summary>IPv4 addresses of the host itself, comma separated (for LAN detection between hosts behind the same NAT).</summary>
    public string? MeshHostIps { get; set; }
    /// <summary>The smallest IP packet size the paths to its peers carry, as last measured (Tune speed); null = never measured.</summary>
    public int? MeshPathMtu { get; set; }

    public bool InMesh => MeshIp != null && MeshPublicKey != null;
    public IEnumerable<string> HostIps => (MeshHostIps ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class RegistryEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>host[:port] without scheme, e.g. registry.example.com:5000</summary>
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Encrypted with DataProtection.</summary>
    public string PasswordProtected { get; set; } = "";
    public bool Insecure { get; set; }
    /// <summary>Other host[:port] names of the same registry, comma separated (e.g. an external address).</summary>
    public string Aliases { get; set; } = "";

    public IEnumerable<string> Hosts => Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Prepend(Url);
    public bool Serves(string host) => Hosts.Contains(host, StringComparer.OrdinalIgnoreCase);
}

public sealed class ProjectEntity
{
    public int Id { get; set; }
    /// <summary>Compose project name (lowercase, [a-z0-9_-]).</summary>
    public string Name { get; set; } = "";
    public int AgentId { get; set; }
    public string ComposeYaml { get; set; } = "";
    public string EnvFile { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastDeployedAt { get; set; }
    public bool? LastDeployOk { get; set; }
    public int HealthTimeoutSeconds { get; set; } = 180;
    /// <summary>Servers the last deploy put services on, comma separated: a service moved elsewhere is taken down there next time.</summary>
    public string DeployedAgentIds { get; set; } = "";

    public IEnumerable<int> DeployedIds => DeployedAgentIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var id) ? id : 0).Where(id => id > 0);
    /// <summary>What each service looked like when it was last deployed: "service=fingerprint;…" (see ComposeServices.Fingerprint).</summary>
    public string DeployedServices { get; set; } = "";

    public Dictionary<string, string> DeployedFingerprints
    {
        get => DeployedServices.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .GroupBy(x => x[0]).ToDictionary(g => g.Key, g => g.Last()[1]);
        set => DeployedServices = string.Join(';', value.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));
    }

    /// <summary>The token of the project's deploy webhook (for CI), encrypted; empty = no webhook.</summary>
    public string HookProtected { get; set; } = "";

    /// <summary>The template this project is an instance of; null for a project made by hand.</summary>
    public int? TemplateId { get; set; }
    /// <summary>An instance's own variables as JSON ({"NAME":"value"}); written into its .env under the template's.</summary>
    public string InstanceVariables { get; set; } = "";

    /// <summary>Replicas the autoscaler runs beyond deploy.replicas: "service=agentId:count,agentId:count;service2=…".</summary>
    public string AutoscaleExtra { get; set; } = "";

    /// <summary>The autoscaler's replicas: service → agent id → how many on that server.</summary>
    public Dictionary<string, IReadOnlyDictionary<int, int>> Extras
    {
        get
        {
            var result = new Dictionary<string, IReadOnlyDictionary<int, int>>();
            foreach (var part in AutoscaleExtra.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var byAgent = new Dictionary<int, int>();
                foreach (var pair in part[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (pair.Split(':') is [var a, var n] && int.TryParse(a, out var id) && int.TryParse(n, out var count) && count > 0) byAgent[id] = count;
                if (byAgent.Count > 0) result[part[..eq]] = byAgent;
            }
            return result;
        }
        set => AutoscaleExtra = string.Join(';', value.Where(s => s.Value.Values.Any(n => n > 0)).OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => s.Key + "=" + string.Join(',', s.Value.Where(x => x.Value > 0).OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"))));
    }
}

public sealed class DeploymentEntity
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Action { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public bool? Ok { get; set; }
    public string Message { get; set; } = "";
    public string Log { get; set; } = "";
    /// <summary>The compose file and .env a deploy put out (empty for other actions and for deploys from before)</summary>
    public string ComposeYaml { get; set; } = "";
    public string EnvFile { get; set; } = "";
    /// <summary>Who started it when it was not a person on the page: "webhook", "rollback to #12"…</summary>
    public string Source { get; set; } = "";
}

/// <summary>A file of a project (nginx.conf, appsettings.json…): written to &lt;project&gt;/files/ on every server it deploys to.</summary>
public sealed class ProjectFileEntity
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ProjectSecretEntity
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    /// <summary>Secret name as referenced from compose (services.x.secrets); file name on the agent.</summary>
    public string Name { get; set; } = "";
    /// <summary>Encrypted with DataProtection.</summary>
    public string ValueProtected { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>An isolated group of servers in the mesh: only members of a common network get each other as peers.</summary>
public sealed class MeshNetworkEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The hub the user chose for this network; null = pick one (a public member).</summary>
    public int? HubAgentId { get; set; }
    /// <summary>Members on one LAN talk without the tunnel (unencrypted): the user trusts the LANs of this network.</summary>
    public bool Trusted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A server in a network; a server may be in several (e.g. one VPS as the shared hub).</summary>
public sealed class MeshNetworkMemberEntity
{
    public int Id { get; set; }
    public int NetworkId { get; set; }
    public int AgentId { get; set; }
}

/// <summary>Something a person may want to know about: a failed deploy, a server gone, a replica short of memory…</summary>
public sealed class EventEntity
{
    public int Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    /// <summary>info, warning or error</summary>
    public string Level { get; set; } = "";
    /// <summary>deploy, server, container, host, service, memory, autoscale, audit, test</summary>
    public string Kind { get; set; } = "";
    public int? ProjectId { get; set; }
    public int? AgentId { get; set; }
    public string? Service { get; set; }
    public string Message { get; set; } = "";
    /// <summary>The container it is about, by name.</summary>
    public string? Container { get; set; }
    /// <summary>More to it, e.g. the last log lines of a container that stopped.</summary>
    public string? Details { get; set; }
    /// <summary>The signed-in user who did it (an audit event), by name: it stays when the user is deleted.</summary>
    public string? User { get; set; }
    /// <summary>Seq took it (or it is from before a Seq address was set).</summary>
    public bool SentToSeq { get; set; }
}

/// <summary>One run of a job (a service with x-dockmaster.schedule): when it started, how it ended.</summary>
public sealed class JobRunEntity
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Service { get; set; } = "";
    public int AgentId { get; set; }
    public string ContainerId { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? ExitCode { get; set; }
    public bool? Ok { get; set; }
    /// <summary>schedule (Dockmaster started it on time), manual (run now), other (a deploy, docker, a person on the server)</summary>
    public string Trigger { get; set; } = "";
    /// <summary>"stopped after the 1h timeout", "out of memory", "retried 2 times"</summary>
    public string Note { get; set; } = "";
    /// <summary>The container's last log lines, kept when the run failed.</summary>
    public string LogTail { get; set; } = "";
}

public sealed class SettingEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// A compose project to start many times over: each instance is a project of its own, made through an API key from
/// the template's compose file and .env plus the variables the caller gives (checked against the template's rules).
/// The caller never sends compose itself, so a key cannot mount the docker socket or touch other projects.
/// </summary>
public sealed class TemplateEntity
{
    public int Id { get; set; }
    /// <summary>Lowercase, [a-z0-9-]; instances are projects named "&lt;template&gt;-&lt;instance&gt;".</summary>
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ComposeYaml { get; set; } = "";
    /// <summary>The .env every instance starts from; the instance's variables are added below it.</summary>
    public string EnvFile { get; set; } = "";
    /// <summary>The variables a caller may set, as JSON (see TemplateVariable).</summary>
    public string Variables { get; set; } = "[]";
    /// <summary>Servers instances may go to, comma separated; empty = any server.</summary>
    public string AgentIds { get; set; } = "";
    /// <summary>The most instances at a time; 0 = no limit.</summary>
    public int MaxInstances { get; set; }
    public int HealthTimeoutSeconds { get; set; } = 180;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public IReadOnlyList<int> AllowedAgentIds => Ids(AgentIds);

    internal static List<int> Ids(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var id) ? id : 0).Where(id => id > 0).Distinct().ToList();
}

/// <summary>A key a program (a game's backend, a CI job) manages instances of some templates with — and nothing else.</summary>
public sealed class ApiKeyEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>SHA-256 of the key, hex; the key itself is shown once, when it is made.</summary>
    public string KeyHash { get; set; } = "";
    /// <summary>The key's last characters, to tell keys apart in the list.</summary>
    public string Hint { get; set; } = "";
    /// <summary>Templates the key may manage instances of, comma separated.</summary>
    public string TemplateIds { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    public IReadOnlyList<int> AllowedTemplateIds => TemplateEntity.Ids(TemplateIds);
}

/// <summary>A person who signs in. An admin may do everything; anyone else only what their grants allow, server by server.</summary>
public sealed class UserEntity
{
    public int Id { get; set; }
    /// <summary>What they sign in with; unique regardless of case.</summary>
    public string Name { get; set; } = "";
    /// <summary>PBKDF2 (see Access.Passwords).</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>Everything: every server, users, settings, registries, the mesh, templates, backups.</summary>
    public bool IsAdmin { get; set; }
    /// <summary>Cannot sign in; signed-in sessions end.</summary>
    public bool Disabled { get; set; }
    /// <summary>Changes with the password (or when the account is disabled): the sessions signed in before end.</summary>
    public string SessionStamp { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>A named set of permissions (see Access.Perm) a user is given on servers.</summary>
public sealed class RoleEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Comma separated Access.Perm keys.</summary>
    public string Permissions { get; set; } = "";

    public IReadOnlyList<string> PermissionList => Access.Perm.Parse(Permissions);
}

/// <summary>A user has a role on a server (<see cref="AgentId"/> 0: on every server, also ones added later).</summary>
public sealed class UserGrantEntity
{
    public const int AllServers = 0;

    public int Id { get; set; }
    public int UserId { get; set; }
    public int AgentId { get; set; }
    public int RoleId { get; set; }
}
