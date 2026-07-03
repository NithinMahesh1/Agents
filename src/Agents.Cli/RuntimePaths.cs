namespace Agents.Cli;

/// <summary>
/// Well-known runtime paths for the CLI. Everything here resolves OUTSIDE the repository so that
/// probe screenshots and the kill-switch handle never land in source control.
/// </summary>
internal static class RuntimePaths
{
    /// <summary>
    /// <c>$XDG_RUNTIME_DIR</c> when it is set and exists (a per-user, tmpfs-backed dir on most Linux
    /// desktops), otherwise the system temp dir (typically <c>/tmp</c>, honouring <c>TMPDIR</c>).
    /// </summary>
    public static string RuntimeDir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(xdg) && Directory.Exists(xdg))
            {
                return xdg;
            }

            return Path.GetTempPath();
        }
    }

    /// <summary>Unix-domain control socket a running <c>agents run</c> listens on for the kill switch.</summary>
    public static string KillSocketPath => Path.Combine(RuntimeDir, "agents.sock");

    /// <summary>Advisory handle file recording the running agent's pid and control-socket path.</summary>
    public static string HandleFilePath => Path.Combine(RuntimeDir, "agents.pid");

    /// <summary>Directory the <c>driver-probe</c> command writes its sample screenshots to.</summary>
    public static string ProbeDir => Path.Combine(RuntimeDir, "agents-probe");
}
