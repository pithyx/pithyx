using System.Globalization;
using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Users;

/// <summary>A Pithyx user as its Linux account should be.</summary>
/// <param name="Name">The user name.</param>
/// <param name="Uid">The uid from the Pithyx range.</param>
/// <param name="Comment">The display name as a passwd comment.</param>
/// <param name="Home">The home folder.</param>
/// <param name="Shell">The login shell.</param>
/// <param name="Expired">True for a deactivated user: the account expired on 1970-01-02, so PAM refuses every login.</param>
public sealed record WantedAccount(string Name, long Uid, string Comment, string Home, string Shell, bool Expired = false);

/// <summary>One step that brings <c>/etc/passwd</c> in line with the database.</summary>
public abstract record AccountAction(string Name);

public sealed record CreateAccount(WantedAccount Account) : AccountAction(Account.Name);

/// <param name="Account">The account as it should be.</param>
/// <param name="Changed">The fields that differ: comment, home, shell, expired.</param>
public sealed record UpdateAccount(WantedAccount Account, IReadOnlyList<string> Changed) : AccountAction(Account.Name);

/// <summary>Removes an account of the managed range that no Pithyx user has any more; its files and home stay.</summary>
public sealed record RemoveAccount(PasswdEntry Entry) : AccountAction(Entry.Name);

/// <param name="Name">The user name.</param>
/// <param name="Reason">
/// <c>name_taken</c> (an account outside the Pithyx range has the name), <c>uid_taken</c> (another account has the
/// uid), <c>uid_changed</c> (the account exists with another uid; uids never change, files depend on them) or
/// <c>uid_outside_range</c>.
/// </param>
public sealed record RefuseAccount(string Name, string Reason) : AccountAction(Name);

/// <summary>
/// The pure difference between the accounts Pithyx wants and the ones <c>/etc/passwd</c> has. Only accounts with a uid
/// in the managed range (20000 to 29999) are ever created, changed or removed; every other account is left alone, and
/// a wanted name that belongs to such an account is refused.
/// </summary>
public static class AccountReconciler
{
    public const long MinUid = 20000;
    public const long MaxUid = 29999;
    public const string NoLogin = "/usr/sbin/nologin";
    public const string NoHome = "/nonexistent";

    /// <summary>The expiry date of a deactivated account, in days since 1970-01-01: long past.</summary>
    public const string ExpiredDate = "1";

    public static bool IsManaged(long uid) => uid is >= MinUid and <= MaxUid;

    /// <param name="wanted">The accounts the users in the database need.</param>
    /// <param name="actual">The accounts in <c>/etc/passwd</c>.</param>
    /// <param name="expiring">The accounts with an expiry date in <c>/etc/shadow</c>.</param>
    public static IReadOnlyList<AccountAction> Plan(IReadOnlyList<WantedAccount> wanted, IReadOnlyList<PasswdEntry> actual, IReadOnlySet<string> expiring)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expiring);
        var byName = actual.GroupBy(entry => entry.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var byUid = actual.GroupBy(entry => entry.Uid).ToDictionary(group => group.Key, group => group.First());
        var wantedNames = wanted.Select(account => account.Name).ToHashSet(StringComparer.Ordinal);
        var actions = new List<AccountAction>();
        // Removals first, so a uid a stale account still holds is free for the account that should have it.
        actions.AddRange(actual.Where(entry => IsManaged(entry.Uid) && !wantedNames.Contains(entry.Name)).Select(entry => new RemoveAccount(entry)));
        var removed = actions.OfType<RemoveAccount>().Select(action => action.Entry.Uid).ToHashSet();
        foreach (var account in wanted)
        {
            if (!IsManaged(account.Uid))
            {
                actions.Add(new RefuseAccount(account.Name, "uid_outside_range"));
            }
            else if (byName.TryGetValue(account.Name, out var existing))
            {
                if (!IsManaged(existing.Uid))
                {
                    actions.Add(new RefuseAccount(account.Name, "name_taken"));
                }
                else if (existing.Uid != account.Uid)
                {
                    actions.Add(new RefuseAccount(account.Name, "uid_changed"));
                }
                else if (Changes(account, existing, expiring.Contains(account.Name)) is { Count: > 0 } changed)
                {
                    actions.Add(new UpdateAccount(account, changed));
                }
            }
            else if (byUid.TryGetValue(account.Uid, out var holder) && !removed.Contains(holder.Uid))
            {
                actions.Add(new RefuseAccount(account.Name, "uid_taken"));
            }
            else
            {
                actions.Add(new CreateAccount(account));
            }
        }
        return actions;
    }

    private static List<string> Changes(WantedAccount account, PasswdEntry entry, bool expired)
    {
        var changed = new List<string>();
        if (account.Comment != entry.Comment)
        {
            changed.Add("comment");
        }
        if (account.Home != entry.Home)
        {
            changed.Add("home");
        }
        if (account.Shell != entry.Shell)
        {
            changed.Add("shell");
        }
        if (account.Expired != expired)
        {
            changed.Add("expired");
        }
        return changed;
    }

    /// <summary>A display name as a passwd comment: no colons, commas or control characters.</summary>
    public static string Comment(string? displayName) =>
        new string((displayName ?? "").Select(c => c is ':' or ',' || char.IsControl(c) ? ' ' : c).ToArray()).Trim();

    /// <summary>The command of an action; refusals have none. Every account gets the group <c>users</c> and no home is created here.</summary>
    public static CommandRequest? Command(AccountAction action) => action switch
    {
        CreateAccount { Account: var account } => new CommandRequest("useradd",
        [
            "--uid", account.Uid.ToString(CultureInfo.InvariantCulture), "--gid", "users", "--no-user-group", "--no-create-home",
            "--home-dir", account.Home, "--shell", account.Shell, "--comment", account.Comment,
            .. account.Expired ? ["--expiredate", ExpiredDate] : Array.Empty<string>(),
            account.Name,
        ]),
        UpdateAccount { Account: var account, Changed: var changed } => new CommandRequest("usermod",
        [
            .. changed.Contains("comment") ? ["--comment", account.Comment] : Array.Empty<string>(),
            .. changed.Contains("home") ? ["--home", account.Home] : Array.Empty<string>(),
            .. changed.Contains("shell") ? ["--shell", account.Shell] : Array.Empty<string>(),
            // An empty date removes the expiry.
            .. changed.Contains("expired") ? ["--expiredate", account.Expired ? ExpiredDate : ""] : Array.Empty<string>(),
            account.Name,
        ]),
        // Never --remove: home data is deleted only after an explicit choice.
        RemoveAccount { Entry: var entry } => new CommandRequest("userdel", [entry.Name]),
        _ => null,
    };
}
