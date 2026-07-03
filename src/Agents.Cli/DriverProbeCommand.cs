using System.Diagnostics;
using Agents.Desktop;

namespace Agents.Cli;

/// <summary>
/// <c>agents driver-probe</c> — a model-independent capability check. It exercises exactly the two
/// driver capabilities a run depends on (screen capture and input injection) with fixed, safe inputs,
/// and flags the specific failure mode that would sink a looped run: the GNOME screenshot portal
/// prompting for permission on every single capture.
/// </summary>
internal static class DriverProbeCommand
{
    // A capture slower than this almost certainly waited on a human dismissing a portal dialog.
    private const double SlowCaptureFloorMs = 1500;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            Help.PrintProbe();
            return 0;
        }

        using var cts = new CancellationTokenSource();
        CliApp.WireCtrlC(cts);
        var ct = cts.Token;

        Console.WriteLine("driver-probe — model-independent capability check");

        var driver = DriverFactory.Create();
        Console.WriteLine($"platform    : {driver.Platform}");

        var outDir = RuntimePaths.ProbeDir;
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"output dir  : {outDir}   (outside the repo)");
        Console.WriteLine();
        Console.WriteLine("capturing three screenshots back-to-back...");

        var durations = new double[3];
        for (var i = 0; i < durations.Length; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var capture = await driver.CaptureAsync(ct);
            stopwatch.Stop();
            durations[i] = stopwatch.Elapsed.TotalMilliseconds;

            var path = Path.Combine(outDir, $"probe-{i + 1}.png");
            await File.WriteAllBytesAsync(path, capture.PngBytes, ct);

            Console.WriteLine(
                $"  capture {i + 1}: {capture.Screen.Width}x{capture.Screen.Height} " +
                $"(scale {capture.Screen.Scale:0.##})  {durations[i],7:0} ms  " +
                $"{capture.PngBytes.Length:n0} bytes");
            Console.WriteLine($"             -> {path}");
        }

        Console.WriteLine();
        ReportTiming(durations);

        Console.WriteLine();
        var info = driver.GetScreenInfo();
        var centreX = Math.Max(1, info.Width / 2);
        var centreY = Math.Max(1, info.Height / 2);
        Console.WriteLine($"input check : moving the mouse to screen centre ({centreX},{centreY})...");
        await driver.MoveMouseAsync(centreX, centreY, ct);

        const string literal = "agents-probe";
        Console.WriteLine($"input check : about to TYPE the fixed literal \"{literal}\" into the focused window.");
        Console.WriteLine("              Focus a scratch text field now — typing in 3s (Ctrl+C to skip)...");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await driver.TypeTextAsync(literal, ct);
            Console.WriteLine($"input check : typed \"{literal}\".");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("input check : skipped (cancelled before typing).");
            return 130;
        }

        Console.WriteLine();
        Console.WriteLine("probe complete: capture + mouse-move + type were all issued without a model.");
        return 0;
    }

    /// <summary>
    /// Interpret the three capture timings. The good case is one-time consent (capture 1 slow, 2/3
    /// fast). The bad case — a per-capture prompt — shows up as capture 2 and/or 3 also being slow,
    /// which is what makes a 25-step run unusable.
    /// </summary>
    private static void ReportTiming(double[] durations)
    {
        var fastest = Math.Min(durations[0], Math.Min(durations[1], durations[2]));

        // "Much slower" = both absolutely slow (a human likely clicked a dialog) and a large multiple
        // of the fastest capture (so we do not warn on ordinary sub-second jitter).
        bool MuchSlower(double ms) => ms > SlowCaptureFloorMs && ms > fastest * 2.5;

        if (MuchSlower(durations[1]) || MuchSlower(durations[2]))
        {
            Console.WriteLine("WARNING: capture 2 and/or 3 were much slower than capture 1.");
            Console.WriteLine("  The screenshot portal is likely prompting for permission on EVERY capture.");
            Console.WriteLine("  A 25-step run would pop a dialog each turn and be unusable. Plan to switch to");
            Console.WriteLine("  a persistent ScreenCast + PipeWire session (one consent for the whole run).");
        }
        else if (durations[0] > SlowCaptureFloorMs)
        {
            Console.WriteLine("NOTE: capture 1 was slow but 2/3 were fast — looks like a one-time consent the");
            Console.WriteLine("  portal remembered. That is the good case for a looped run.");
        }
        else
        {
            Console.WriteLine("OK: all three captures were fast — no per-capture permission prompt detected.");
        }
    }
}
