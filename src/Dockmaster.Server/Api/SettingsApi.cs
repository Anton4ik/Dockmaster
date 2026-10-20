using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

public static class SettingsApi
{
    public static void MapSettingsApi(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/settings").RequireAuthorization().ForAdmin();

        g.MapGet("/", async (SettingsService settings, CertificateService certs, IConfiguration config, IDbContextFactory<DockmasterDb> dbf, HttpContext ctx, CancellationToken ct) =>
        {
            var port = config["AgentPort"] ?? SettingsService.DefaultAgentPort.ToString();
            var url = await settings.GetAsync(SettingsService.AgentPublicUrlKey, ct) ?? $"https://{ctx.Request.Host.Host}:{port}";
            var mesh = await settings.MeshAsync(ct);
            await using var db = await dbf.CreateDbContextAsync(ct);
            var inUse = await MeshInUseAsync(db, ct);
            return new SettingsDto(url, await settings.AgentImageAsync(ct), certs.Fingerprint, certs.FingerprintPretty, mesh.Subnet, mesh.Port, inUse, mesh.Mtu, mesh.TcpTuning);
        });

        g.MapPut("/", async (SaveSettingsRequest req, HttpContext ctx, SettingsService settings, MeshService meshService, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            var current = await settings.MeshAsync(ct);
            var subnet = (req.MeshSubnet ?? current.Subnet).Trim();
            var port = req.MeshPort ?? current.Port;
            var mtu = req.MeshMtu ?? current.Mtu;
            var tuning = req.MeshTcpTuning ?? current.TcpTuning;
            if (SubnetAllocator.Validate(subnet) is { } err) return ApiErrors.Bad(err);
            if (MeshService.ValidatePort(port) is { } portErr) return ApiErrors.Bad(portErr);
            if (mtu != 0 && mtu is < MeshPlanner.MinMtu or > MeshPlanner.MaxMtu) return ApiErrors.Bad($"the tunnel MTU is between {MeshPlanner.MinMtu} and {MeshPlanner.MaxMtu}, or empty for automatic");
            if (subnet != current.Subnet || port != current.Port)
            {
                await using var db = await dbf.CreateDbContextAsync(ct);
                // addresses and ports are handed out at join time: changing them under joined servers would leave them inconsistent
                if (await MeshInUseAsync(db, ct)) return ApiErrors.Bad("the mesh subnet and port cannot change while servers are in the mesh — leave the mesh on every server first");
            }
            // everything is checked before anything is stored: a refused request changes nothing
            await settings.SetManyAsync(new Dictionary<string, string>
            {
                [SettingsService.AgentPublicUrlKey] = (req.AgentPublicUrl ?? "").Trim().TrimEnd('/'),
                [SettingsService.AgentImageKey] = (req.AgentImage ?? "").Trim(),
                [SettingsService.MeshSubnetKey] = subnet,
                [SettingsService.MeshPortKey] = port.ToString(),
                [SettingsService.MeshMtuKey] = mtu > 0 ? mtu.ToString() : "",
                [SettingsService.MeshTcpTuningKey] = tuning ? "1" : "0",
            }, ct);
            // the hosts get their new configs now, not at the next reconcile
            if (mtu != current.Mtu || tuning != current.TcpTuning) await meshService.PushAllAsync(null, ct);
            await ctx.AuditAsync("saved the settings");
            return Results.Ok();
        });
    }

    private static Task<bool> MeshInUseAsync(DockmasterDb db, CancellationToken ct) => db.Agents.AnyAsync(a => a.MeshIp != null, ct);
}
