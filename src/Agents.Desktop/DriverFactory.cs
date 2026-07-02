using System.Runtime.InteropServices;
using Agents.Core;

namespace Agents.Desktop;

/// <summary>Selects and constructs the <see cref="IDesktopDriver"/> for the current OS.</summary>
public static class DriverFactory
{
    /// <summary>
    /// Creates the desktop driver appropriate for the running platform: Linux →
    /// <see cref="LinuxWaylandDriver"/>, Windows → <see cref="WindowsDriver"/>, macOS →
    /// <see cref="MacDriver"/>.
    /// </summary>
    /// <param name="runner">
    /// Optional process runner forwarded to the Linux driver (defaults to
    /// <see cref="DefaultProcessRunner"/>). The Windows and macOS skeletons ignore it.
    /// </param>
    /// <exception cref="PlatformNotSupportedException">Thrown on unrecognised platforms.</exception>
    public static IDesktopDriver Create(IProcessRunner? runner = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxWaylandDriver(runner);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsDriver();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacDriver();
        }

        throw new PlatformNotSupportedException(
            "No desktop driver is available for the current OS platform.");
    }
}
