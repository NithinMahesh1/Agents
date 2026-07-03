using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Agents.Core;

namespace Agents.Desktop;

/// <summary>
/// <see cref="IDesktopDriver"/> for Wayland sessions. Input is injected via the <c>ydotool</c>
/// CLI (through <see cref="IProcessRunner"/>, argv only — never a shell). Screen capture goes
/// through the XDG Desktop Portal <c>org.freedesktop.portal.Screenshot</c> interface driven by
/// the <c>gdbus</c> CLI; we deliberately avoid the Tmds.DBus NuGet package (CVE-2026-39959).
/// </summary>
public sealed partial class LinuxWaylandDriver : IDesktopDriver
{
    private const string PortalBusName = "org.freedesktop.portal.Desktop";
    private const string PortalObjectPath = "/org/freedesktop/portal/desktop";

    // The portal can fulfil an interactive:false screenshot almost immediately, so we start the
    // signal monitor first and give it a moment to install its bus match rule before calling
    // Screenshot(); this closes the race where the Response fires before we are listening.
    private static readonly TimeSpan WarmupDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(30);

    // TODO: derive real geometry from the compositor/portal. Until a capture populates the cache,
    // GetScreenInfo() returns this documented default.
    private static readonly ScreenInfo DefaultScreenInfo = new(1920, 1080, 1.0);

    private readonly IProcessRunner _runner;
    private ScreenInfo? _cachedScreen;

    /// <summary>Creates the driver, optionally with a custom <see cref="IProcessRunner"/>.</summary>
    /// <param name="runner">Process runner to use; defaults to <see cref="DefaultProcessRunner"/>.</param>
    public LinuxWaylandDriver(IProcessRunner? runner = null) => _runner = runner ?? new DefaultProcessRunner();

    /// <inheritdoc />
    public string Platform => "linux-wayland";

    /// <inheritdoc />
    /// <remarks>
    /// Returns the geometry of the most recent capture, or a documented default before any
    /// capture has happened.
    /// </remarks>
    public ScreenInfo GetScreenInfo() => _cachedScreen ?? DefaultScreenInfo;

    /// <inheritdoc />
    public async Task MoveMouseAsync(int x, int y, CancellationToken ct = default)
    {
        // ydotool mousemove --absolute -x <x> -y <y>
        await RunYdotoolAsync(
            ["mousemove", "--absolute", "-x", Fmt(x), "-y", Fmt(y)],
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClickAsync(MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        // ydotool click <code>; 0xC0 encodes "down+up" of the left button (0x40 down | 0x80 up),
        // 0xC1 right, 0xC2 middle.
        await RunYdotoolAsync(["click", ClickCode(button)], ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DoubleClickAsync(MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        // Two clicks in a single ydotool invocation to keep them within the double-click interval.
        var code = ClickCode(button);
        await RunYdotoolAsync(["click", code, code], ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DragAsync(
        int fromX,
        int fromY,
        int toX,
        int toY,
        MouseButton button = MouseButton.Left,
        CancellationToken ct = default)
    {
        // A drag is four ordered, separate ydotool invocations so the button stays held across the
        // move: position at the source, press-and-hold (down-only code), move to the destination
        // (dragging), then release (up-only code). Coordinates are absolute screenshot pixels, per
        // the Core coordinate contract, so both moves reuse MoveMouseAsync (mousemove --absolute).
        await MoveMouseAsync(fromX, fromY, ct).ConfigureAwait(false);
        await RunYdotoolAsync(["click", MouseCode(MouseButtonDown, button)], ct).ConfigureAwait(false);
        await MoveMouseAsync(toX, toY, ct).ConfigureAwait(false);
        await RunYdotoolAsync(["click", MouseCode(MouseButtonUp, button)], ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TypeTextAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Flag-injection defense: `--` terminates ydotool's option parsing and <text> is passed as a
        // single argv element (ProcessStartInfo.ArgumentList, never a shell string). So model-typed
        // text that looks like a flag — e.g. "--file ~/.ssh/id_ed25519" — is typed verbatim instead
        // of being interpreted by ydotool (which would otherwise read a file into the keystroke stream).
        await RunYdotoolAsync(["type", "--", text], ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task KeyPressAsync(string keyCombo, CancellationToken ct = default)
    {
        // Strict choke point: KeyCombo rejects anything outside the allowlist grammar — VT-switch
        // (ctrl+alt+F1..F12), ctrl+alt+Del, the ctrl+alt+BackSpace X "zap", SysRq, unknown
        // modifiers/keys — by throwing BEFORE we translate or emit, so a rejected combo never
        // reaches ydotool. Anything returned here is guaranteed to resolve in KeyMap.
        var parts = KeyCombo.Validate(keyCombo);

        var codes = new int[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            codes[i] = KeyMap.GetCode(parts[i]);
        }

        // Press all keys down in order, then release in reverse: e.g. "ctrl+c" -> 29:1 46:1 46:0 29:0.
        var args = new List<string>((codes.Length * 2) + 1) { "key" };
        foreach (var code in codes)
        {
            args.Add($"{code}:1");
        }

        for (var i = codes.Length - 1; i >= 0; i--)
        {
            args.Add($"{codes[i]}:0");
        }

        await RunYdotoolAsync(args, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ScrollAsync(int dx, int dy, CancellationToken ct = default)
    {
        // Sign convention: Core specifies screen-scroll direction — positive dy = DOWN, positive
        // dx = RIGHT. ydotool drives the wheel through evdev REL_WHEEL/REL_HWHEEL, whose vertical
        // sign is inverted (REL_WHEEL positive = UP), so we negate dy; REL_HWHEEL positive = right,
        // which already matches dx.
        //
        // TODO: `mousemove --wheel` exists only on newer ydotool builds; older builds have no wheel
        //       subcommand at all. On those, emit REL_WHEEL/REL_HWHEEL directly via uinput (the
        //       ydotoold protocol) or fall back to a compositor-specific tool. Also confirm the sign
        //       on the target build: some builds already flip REL_WHEEL to screen-scroll direction,
        //       in which case the dy negation below must be removed.
        await RunYdotoolAsync(
            ["mousemove", "--wheel", "-x", Fmt(dx), "-y", Fmt(-dy)],
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ScreenCapture> CaptureAsync(CancellationToken ct = default)
    {
        var handleToken = "agents_" + Guid.NewGuid().ToString("N")[..12];
        var uri = await CapturePortalScreenshotAsync(handleToken, ct).ConfigureAwait(false);

        // The portal returns a file:// URI (typically under the document portal or $XDG_RUNTIME_DIR).
        var path = new Uri(uri).LocalPath;
        var png = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

        var (width, height) = ReadPngSize(png);
        var screen = new ScreenInfo(width, height, 1.0);
        _cachedScreen = screen;
        return new ScreenCapture(png, screen);
    }

    private async Task RunYdotoolAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await _runner.RunAsync("ydotool", args, ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // Detailed error stays server-side; callers see a generic operation failure.
            throw new InvalidOperationException(
                $"ydotool {args[0]} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
        }
    }

    // ydotool `click` takes one byte: the low bits select the button (left 0, right 1, middle 2)
    // and the high bits are the edges — 0x40 = press, 0x80 = release, 0xC0 = both. ClickAsync sends
    // the combined edge (0xC0/0xC1/0xC2); a drag needs the press and release as separate
    // invocations so the button stays held while the pointer moves between them.
    private const int MouseButtonDown = 0x40;
    private const int MouseButtonUp = 0x80;

    private static int ButtonIndex(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button."),
    };

    private static string MouseCode(int edges, MouseButton button) =>
        "0x" + (edges | ButtonIndex(button)).ToString("X2", CultureInfo.InvariantCulture);

    // Down|Up — preserves the original 0xC0/0xC1/0xC2 click codes.
    private static string ClickCode(MouseButton button) => MouseCode(MouseButtonDown | MouseButtonUp, button);

    private static string Fmt(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Drives the XDG Desktop Portal screenshot flow via <c>gdbus</c> and returns the resulting
    /// <c>file://</c> URI. The portal is asynchronous: <c>Screenshot()</c> returns a request object
    /// path, and the actual result arrives later as an <c>org.freedesktop.portal.Request.Response</c>
    /// signal on that path. We therefore start a signal monitor first, invoke Screenshot(), then
    /// read the monitor's output until the matching Response appears.
    /// </summary>
    /// <remarks>
    /// The streaming monitor is intentionally spawned via <see cref="Process"/> rather than
    /// <see cref="IProcessRunner"/>: the runner is run-to-completion, whereas here we must react to
    /// the Response line and stop early. TODO: if a streaming seam is later added, route this
    /// through it for symmetry and unit-testability.
    /// </remarks>
    private async Task<string> CapturePortalScreenshotAsync(string handleToken, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ScreenshotTimeout);
        var token = timeoutCts.Token;

        using var monitor = StartPortalMonitor();
        try
        {
            // Let gdbus install its match rule before the portal can emit the Response.
            await Task.Delay(WarmupDelay, token).ConfigureAwait(false);

            var requestPath = await InvokeScreenshotAsync(handleToken, ct).ConfigureAwait(false);

            var reader = monitor.StandardOutput;
            string? line;
            while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
            {
                if (!line.Contains("Request.Response", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!line.Contains(requestPath, StringComparison.Ordinal) &&
                    !line.Contains(handleToken, StringComparison.Ordinal))
                {
                    continue;
                }

                var code = ParseResponseCode(line);
                if (code != 0)
                {
                    throw new InvalidOperationException(
                        $"Screenshot request was not fulfilled (portal response code {code}; " +
                        "1 = user cancelled, 2 = other error).");
                }

                return ParseUri(line)
                    ?? throw new InvalidOperationException("Screenshot Response did not contain a 'uri' value.");
            }

            throw new InvalidOperationException("The screenshot portal closed without sending a Response.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {ScreenshotTimeout.TotalSeconds:0}s waiting for the desktop portal screenshot Response.");
        }
        finally
        {
            TryKill(monitor);
        }
    }

    private static Process StartPortalMonitor()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gdbus",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in new[] { "monitor", "--session", "--dest", PortalBusName })
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    private async Task<string> InvokeScreenshotAsync(string handleToken, CancellationToken ct)
    {
        // a{sv} options; variants are wrapped in <...> as gdbus expects.
        var options = $"{{'interactive': <false>, 'handle_token': <'{handleToken}'>}}";

        IReadOnlyList<string> args =
        [
            "call", "--session",
            "--dest", PortalBusName,
            "--object-path", PortalObjectPath,
            "--method", "org.freedesktop.portal.Screenshot.Screenshot",
            string.Empty, // parent_window
            options,      // options a{sv}
        ];

        var result = await _runner.RunAsync("gdbus", args, ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gdbus Screenshot call failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
        }

        var stdout = Encoding.UTF8.GetString(result.StdOut);
        var match = RequestPathRegex().Match(stdout);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Could not parse the request object path from gdbus output: {stdout.Trim()}");
        }

        return match.Groups[1].Value;
    }

    private static string? ParseUri(string line)
    {
        var match = UriRegex().Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static uint ParseResponseCode(string line)
    {
        var match = ResponseCodeRegex().Match(line);
        return match.Success && uint.TryParse(match.Groups[1].Value, out var code) ? code : 0u;
    }

    /// <summary>
    /// Reads a PNG's pixel dimensions straight from its IHDR chunk: an 8-byte signature followed
    /// by a chunk whose data holds a big-endian width at byte offset 16 and height at offset 20.
    /// </summary>
    private static (int Width, int Height) ReadPngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 ||
            png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47 ||
            png[4] != 0x0D || png[5] != 0x0A || png[6] != 0x1A || png[7] != 0x0A)
        {
            throw new InvalidDataException("Screenshot data is not a valid PNG (bad signature).");
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        return ((int)width, (int)height);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup; the monitor is short-lived and bounded by the timeout anyway.
        }
    }

    [GeneratedRegex(@"objectpath '([^']+)'")]
    private static partial Regex RequestPathRegex();

    [GeneratedRegex(@"'uri':\s*<'([^']+)'>")]
    private static partial Regex UriRegex();

    [GeneratedRegex(@"Request\.Response \(uint32 (\d+)")]
    private static partial Regex ResponseCodeRegex();
}
