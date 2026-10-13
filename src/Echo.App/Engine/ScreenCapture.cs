using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Echo.Core;
using WinForms = System.Windows.Forms;

namespace Echo.App.Engine;

/// <summary>
/// Screen capture for Echo (F1) — faithful port of <c>screen_capture.py</c>.
///
/// Two uses:
///   1) AUTO: during a Teams meeting, saves an image on each notable screen change
///      (new slide / shared content) while the Teams window is FOREGROUND. Images →
///      <c>&lt;session&gt;\Captures</c>.
///   2) MANUAL: <see cref="CaptureNow"/> photographs the screen on demand.
///
/// Robust, config-free choices mirroring Python:
///   - grabs ALL monitors at once (GDI+ CopyFromScreen over the virtual screen) →
///     robust to multi-monitor + DPI scaling;
///   - change detection via a 32×32 grayscale signature + MAD (mean absolute
///     difference): a new slide moves many pixels, video-gallery motion stays low;
///   - in AUTO, captures ONLY when Teams is foreground → never captures the user's
///     other apps (privacy + less noise).
///
/// Assumed limit (as in Python): the FIRST frame is the reference only (never
/// saved), so a slide already on screen at start and never changed is not
/// auto-captured — that is the manual button's job.
/// </summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);

    private const int DwmwaExtendedFrameBounds = 9;   // the VISIBLE bounds, without the invisible resize border

    /// <summary>
    /// On-screen bounds of a window in the SAME coordinate space as <see cref="Grab"/>
    /// (both go through this process's DPI-awareness lens, so a later crop lines up).
    /// Prefers DWM extended frame bounds (excludes the ~7 px invisible border); null if
    /// the handle is invalid or both queries fail.
    /// </summary>
    public static Rectangle? WindowBounds(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;
        try
        {
            if (DwmGetWindowAttribute(hWnd, DwmwaExtendedFrameBounds, out var d, Marshal.SizeOf<RECT>()) == 0
                && d.Right > d.Left && d.Bottom > d.Top)
                return Rectangle.FromLTRB(d.Left, d.Top, d.Right, d.Bottom);
        }
        catch { /* dwmapi unavailable → GetWindowRect */ }
        try
        {
            if (GetWindowRect(hWnd, out var r) && r.Right > r.Left && r.Bottom > r.Top)
                return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>
    /// Translate an on-screen window rectangle into pixel coordinates inside a full-screen
    /// grab whose top-left is <paramref name="virtualOrigin"/>, clamped to the bitmap. Returns
    /// <see cref="Rectangle.Empty"/> when the visible overlap is too small to be a real window
    /// (caller then keeps the full grab). Pure + unit-testable (no Win32, no DPI).
    /// </summary>
    public static Rectangle CropRect(Point virtualOrigin, Size bmp, Rectangle window, int minSide = 120)
    {
        int x = window.Left - virtualOrigin.X;
        int y = window.Top - virtualOrigin.Y;
        int left = Math.Clamp(x, 0, bmp.Width);
        int top = Math.Clamp(y, 0, bmp.Height);
        int right = Math.Clamp(x + window.Width, 0, bmp.Width);
        int bottom = Math.Clamp(y + window.Height, 0, bmp.Height);
        int w = right - left, h = bottom - top;
        if (w < minSide || h < minSide) return Rectangle.Empty;
        return new Rectangle(left, top, w, h);
    }

    /// <summary>Crop <paramref name="full"/> to <paramref name="hWnd"/>'s visible bounds; returns
    /// the same bitmap untouched when the window cannot be resolved or is too small (fallback to
    /// full screen). The caller owns disposal of whichever bitmap comes back.</summary>
    private static Bitmap CropToWindow(Bitmap full, Rectangle virtualScreen, IntPtr hWnd)
    {
        var wb = WindowBounds(hWnd);
        if (wb is null) return full;
        var crop = CropRect(virtualScreen.Location, full.Size, wb.Value);
        if (crop.IsEmpty || (crop.Width == full.Width && crop.Height == full.Height)) return full;
        try
        {
            var cropped = full.Clone(crop, full.PixelFormat);
            full.Dispose();
            return cropped;
        }
        catch { return full; }
    }

    /// <summary>PID of the foreground window's process (null on failure).</summary>
    public static int? ForegroundPid()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }
        catch { return null; }
    }

    /// <summary>RGB grab of the whole virtual screen (all monitors).</summary>
    public static Bitmap Grab()
    {
        var b = WinForms.SystemInformation.VirtualScreen;
        int w = Math.Max(1, b.Width), h = Math.Max(1, b.Height);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(b.Left, b.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>32×32 grayscale signature (ITU-R 601-2 luma, like PIL "L"), for comparing captures.</summary>
    private static byte[] Signature(Image img)
    {
        using var small = new Bitmap(32, 32, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(img, new Rectangle(0, 0, 32, 32));
        }
        var sig = new byte[32 * 32];
        int k = 0;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                var c = small.GetPixel(x, y);
                sig[k++] = (byte)((c.R * 299 + c.G * 587 + c.B * 114) / 1000);
            }
        return sig;
    }

    /// <summary>Mean absolute difference between two signatures (large = changed a lot).</summary>
    private static double Mad(byte[]? a, byte[]? b)
    {
        if (a is null || b is null || a.Length != b.Length) return 999.0;
        long s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return (double)s / a.Length;
    }

    /// <summary>Save the image as a time-stamped PNG in <paramref name="folder"/>. Returns the path.</summary>
    public static string SaveCapture(Image img, string folder, string prefix = "Capture")
    {
        Directory.CreateDirectory(folder);
        var now = DateTime.Now;
        string bfn = $"{prefix} - {now:HH}h{now:mm}m{now:ss}";
        string path = Path.Combine(folder, bfn + ".png");
        int i = 2;
        while (File.Exists(path)) { path = Path.Combine(folder, $"{bfn} ({i}).png"); i++; } // avoid same-second overwrite
        img.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>Immediate capture (manual button). With <paramref name="windowOnly"/>, crops to the
    /// foreground window (typically Teams when the user hits capture). Returns the path, or null.</summary>
    public static string? CaptureNow(string folder, string prefix = "Capture", bool windowOnly = false)
    {
        try
        {
            var vs = WinForms.SystemInformation.VirtualScreen;
            var img = Grab();
            if (windowOnly) img = CropToWindow(img, vs, GetForegroundWindow());
            try { return SaveCapture(img, folder, prefix); }
            finally { img.Dispose(); }
        }
        catch (Exception ex) { Log.Error("capture_now", ex); return null; }
    }

    /// <summary>
    /// AUTO loop. Until <paramref name="ct"/> is cancelled: samples the screen and saves an
    /// image when the content changes notably AND Teams is foreground.
    ///   <paramref name="getFolder"/>  → target folder (null = skip this tick);
    ///   <paramref name="isEnabled"/>  → is the option active (read live);
    ///   <paramref name="teamsPids"/>  → set of Teams PIDs (for the foreground test).
    /// </summary>
    public static async Task RunAutoCaptureAsync(
        Func<string?> getFolder, Func<bool> isEnabled, Func<IReadOnlyList<int>> teamsPids, CancellationToken ct,
        Func<bool>? windowOnly = null)
    {
        byte[]? lastSig = null;
        double lastSave = 0.0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Info("auto-capture started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(EchoConstants.CapturePollSeconds), ct); }
                catch (TaskCanceledException) { break; }
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (!isEnabled()) { lastSig = null; continue; } // recalibrate reference if re-enabled later
                    var pids = teamsPids() ?? (IReadOnlyList<int>)Array.Empty<int>();
                    var fgHwnd = GetForegroundWindow();
                    GetWindowThreadProcessId(fgHwnd, out uint fgPid);
                    if (fgPid == 0 || !pids.Contains((int)fgPid)) continue; // Teams not foreground → capture nothing
                    var folder = getFolder();
                    if (string.IsNullOrEmpty(folder)) continue;
                    // Crop to the (foreground) Teams window so the capture is the meeting content
                    // only, not the taskbar / a second monitor. Full-screen fallback if unresolved.
                    var vs = WinForms.SystemInformation.VirtualScreen;
                    var img = Grab();
                    if (windowOnly is null || windowOnly()) img = CropToWindow(img, vs, fgHwnd);
                    try
                    {
                        var sig = Signature(img);
                        if (lastSig is not null)
                        {
                            double now = sw.Elapsed.TotalSeconds;
                            if (Mad(sig, lastSig) >= EchoConstants.CaptureChangeThreshold
                                && (now - lastSave) >= EchoConstants.CaptureMinIntervalSeconds)
                            {
                                var p = SaveCapture(img, folder!, "Slide");
                                lastSave = now;
                                Log.Info($"auto capture -> {Path.GetFileName(p)}");
                            }
                        }
                        lastSig = sig;
                    }
                    finally { img.Dispose(); }
                }
                catch (Exception ex) { Log.Error("auto-capture", ex); }
            }
        }
        finally { Log.Info("auto-capture stopped"); }
    }

    /// <summary>
    /// Re-encode an image file as a resized JPEG data-URI (thumbnail ≤ <paramref name="maxPx"/> px,
    /// quality <paramref name="quality"/>) for inline display without <c>file://</c> (CSP-safe).
    /// Mirrors <c>get_last_capture</c> (420/70) and <c>_captures_data</c> (900/72). Null on error.
    /// </summary>
    public static string? ThumbnailDataUri(string path, int maxPx, long quality)
    {
        try
        {
            using var src = new Bitmap(path);
            var (w, h) = Fit(src.Width, src.Height, maxPx);
            using var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            using var ms = new MemoryStream();
            var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            dst.Save(ms, enc, ep);
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex) { Log.Error($"thumbnail {Path.GetFileName(path)}", ex); return null; }
    }

    /// <summary>Aspect-preserving fit that only downsizes (like PIL Image.thumbnail).</summary>
    private static (int W, int H) Fit(int w, int h, int max)
    {
        if (w <= max && h <= max) return (w, h);
        double r = Math.Min((double)max / w, (double)max / h);
        return (Math.Max(1, (int)Math.Round(w * r)), Math.Max(1, (int)Math.Round(h * r)));
    }
}
