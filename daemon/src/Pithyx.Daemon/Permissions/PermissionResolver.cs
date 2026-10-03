using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Permissions;

/// <summary>
/// What a principal may do. Administrators may do everything: every share with read and write, every protocol, app
/// and right. Everybody else has what its grants allow and nothing more.
/// </summary>
/// <param name="IsAdmin">Whether the principal has the administrator right.</param>
/// <param name="Shares">The access to each share it was granted, never <see cref="ShareAccess.None"/>.</param>
/// <param name="Allowed">The resources other than shares it was granted.</param>
public sealed record EffectivePermissions(bool IsAdmin, IReadOnlyDictionary<long, ShareAccess> Shares, IReadOnlySet<string> Allowed)
{
    public static readonly EffectivePermissions None = new(false, new Dictionary<long, ShareAccess>(), new HashSet<string>(StringComparer.Ordinal));

    public ShareAccess Share(long shareId) => IsAdmin ? ShareAccess.ReadWrite : Shares.GetValueOrDefault(shareId, ShareAccess.None);

    public bool Has(string resource) => IsAdmin || Allowed.Contains(resource);
}

/// <summary>
/// The permission model as a pure function (step 11, D290): the effective rights are the union of the principal's own
/// grants and those of its groups. There are no deny rules, so another grant can only add; a deactivated user has
/// nothing.
/// </summary>
public static class PermissionResolver
{
    public static EffectivePermissions Resolve(Principal principal, IEnumerable<Grant> grants)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(grants);
        if (!principal.Active)
        {
            return EffectivePermissions.None;
        }
        var groups = principal.Kind == SubjectKind.User ? principal.GroupIds.ToHashSet() : [];
        var shares = new Dictionary<long, ShareAccess>();
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            var applies = (grant.SubjectKind == principal.Kind && grant.SubjectId == principal.Id)
                || (grant.SubjectKind == SubjectKind.Group && groups.Contains(grant.SubjectId));
            if (!applies || grant.Level == ShareAccess.None)
            {
                continue;
            }
            if (Resources.ShareId(grant.Resource) is { } shareId)
            {
                shares[shareId] = shares.TryGetValue(shareId, out var current) && current > grant.Level ? current : grant.Level;
            }
            else
            {
                allowed.Add(grant.Resource);
            }
        }
        // Only people administer the box; an app never becomes an administrator through a grant.
        var admin = principal.Kind == SubjectKind.User && allowed.Contains(Resources.Admin);
        return new EffectivePermissions(admin, shares, allowed);
    }
}
