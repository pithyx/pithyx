using System.Text;

namespace Pithyx.Daemon.FileServices;

/// <summary>
/// Renders the sshd drop-in for Pithyx accounts (D288). Every account Pithyx manages is in <see cref="UsersGroup"/>;
/// those with the SFTP right, while SFTP is on, are also in <see cref="SftpGroup"/>, and those with the SSH login right,
/// while SSH logins are on, in <see cref="SshGroup"/> instead (D290): they get a shell. SFTP users get
/// <c>internal-sftp</c> in a chroot that shows only their shared folders (<see cref="SftpViews"/>), with a password and
/// without a shell, forwarding or a terminal. Every other Pithyx account has no way to sign in over SSH. Accounts
/// outside Pithyx (the owner's own, root) are not touched; whether sshd runs at all belongs to the SSH setting.
/// </summary>
public static class SshdConfig
{
    public const string DropInPath = "/etc/ssh/sshd_config.d/50-pithyx.conf";

    /// <summary>The privilege separation directory: <c>sshd -t</c> refuses every configuration while it is missing, and only
    /// <c>ssh.service</c> creates it, which never ran on a fresh install.</summary>
    public const string PrivilegeSeparationPath = "/run/sshd";

    public const string UsersGroup = "pithyx-users";

    public const string SftpGroup = "pithyx-sftp";

    /// <summary>The users with the SSH login right while SSH logins are on (D290); never also in <see cref="SftpGroup"/>.</summary>
    public const string SshGroup = "pithyx-ssh";

    public const string Header = "# Managed by pithyx. Pithyx writes this file again on every change; put your own settings into another file in this folder.";

    /// <param name="chrootRoot">The folder that holds one chroot per user, for example <c>/run/pithyx/sftp</c>.</param>
    public static string Render(string chrootRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(chrootRoot);
        var text = new StringBuilder();
        text.AppendLine(Header);
        text.AppendLine();
        text.AppendLine($"Match Group {SftpGroup}");
        text.AppendLine($"    ChrootDirectory {chrootRoot}/%u");
        text.AppendLine("    ForceCommand internal-sftp -u 0007");
        text.AppendLine("    PasswordAuthentication yes");
        text.AppendLine("    PubkeyAuthentication no");
        Closed(text);
        text.AppendLine();
        // Users who sign in with a shell: a password and a terminal, nothing forwarded.
        text.AppendLine($"Match Group {SshGroup}");
        text.AppendLine("    PasswordAuthentication yes");
        text.AppendLine("    PubkeyAuthentication yes");
        text.AppendLine("    AllowTcpForwarding no");
        text.AppendLine("    AllowAgentForwarding no");
        text.AppendLine("    AllowStreamLocalForwarding no");
        text.AppendLine("    X11Forwarding no");
        text.AppendLine("    PermitTunnel no");
        text.AppendLine();
        text.AppendLine($"Match Group {UsersGroup},!{SftpGroup},!{SshGroup}");
        text.AppendLine("    PasswordAuthentication no");
        text.AppendLine("    PubkeyAuthentication no");
        text.AppendLine("    KbdInteractiveAuthentication no");
        text.AppendLine("    HostbasedAuthentication no");
        text.AppendLine("    GSSAPIAuthentication no");
        Closed(text);
        return text.ToString();
    }

    private static void Closed(StringBuilder text)
    {
        text.AppendLine("    AllowTcpForwarding no");
        text.AppendLine("    AllowAgentForwarding no");
        text.AppendLine("    AllowStreamLocalForwarding no");
        text.AppendLine("    X11Forwarding no");
        text.AppendLine("    PermitTunnel no");
        text.AppendLine("    PermitTTY no");
    }
}
