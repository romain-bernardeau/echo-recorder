using System.Runtime.InteropServices;

namespace Echo.App;

/// <summary>
/// Win32 helpers for the frameless windows. Dragging a top-level window from a
/// mousedown inside the WebView2 child HWND is done with the classic
/// ReleaseCapture + WM_NCLBUTTONDOWN(HTCAPTION) trick (WPF DragMove from a child
/// hwnd is unreliable). See docs/contract/platform.md §7 threading note.
/// </summary>
internal static class Native
{
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Begin a native window-move loop for the given top-level HWND.</summary>
    public static void BeginDrag(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }
}
