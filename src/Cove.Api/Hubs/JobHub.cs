using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;

namespace Cove.Api.Hubs;

public class JobHub(ICurrentPrincipalAccessor principalAccessor, CoveContext db) : Hub
{
    internal const string GlobalGroup = "jobs:global";

    internal static string OwnerGroup(JobOwner owner) => $"jobs:{owner.Key}";

    public override async Task OnConnectedAsync()
    {
        var principal = principalAccessor.Current;
        if (principal is null || principal.Kind == PrincipalKind.Anonymous)
        {
            Context.Abort();
            return;
        }

        var owner = JobOwner.FromPrincipal(principal);
        var canReadGlobal = await CanReadGlobalStreamAsync(
            principal, Permissions.JobsRead, db, Context.ConnectionAborted);
        if (!canReadGlobal && owner is null)
        {
            Context.Abort();
            return;
        }

        if (canReadGlobal)
            await Groups.AddToGroupAsync(Context.ConnectionId, GlobalGroup, Context.ConnectionAborted);
        if (owner is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, OwnerGroup(owner), Context.ConnectionAborted);

        await Clients.Caller.SendAsync("ConnectionEstablished", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    internal static async Task<bool> CanReadGlobalStreamAsync(
        CovePrincipal? principal, string permission, CoveContext db, CancellationToken cancellationToken)
    {
        if (principal is null || principal.Kind == PrincipalKind.Anonymous || !principal.Has(permission))
            return false;
        if (principal.Has(Permissions.All))
            return true;
        if (principal.ReadRestrictedEntityKinds.Count > 0)
            return false;

        var roleNames = principal.Roles.ToArray();
        return !await db.RoleContentRules.AsNoTracking().AnyAsync(rule =>
                rule.Role != null && roleNames.Contains(rule.Role.Name)
                && rule.Effect == "deny"
                && (rule.AppliesTo == "read" || rule.AppliesTo == "all"), cancellationToken)
            && !await db.RoleEntityOverrides.AsNoTracking().AnyAsync(overrideItem =>
                overrideItem.Role != null && roleNames.Contains(overrideItem.Role.Name)
                && overrideItem.Effect == "deny"
                && (overrideItem.AppliesTo == "read" || overrideItem.AppliesTo == "all"), cancellationToken);
    }
}

public class LogHub(ICurrentPrincipalAccessor principalAccessor, CoveContext db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (!await JobHub.CanReadGlobalStreamAsync(
                principalAccessor.Current, Permissions.AuditRead, db, Context.ConnectionAborted))
        {
            Context.Abort();
            return;
        }

        await Clients.Caller.SendAsync("ConnectionEstablished", Context.ConnectionId);
        await base.OnConnectedAsync();
    }
}
