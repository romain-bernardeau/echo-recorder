using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Echo.Teams.Internal;

/// <summary>A rectangle as (left, top, right, bottom) in screen pixels.</summary>
internal readonly record struct RectD(double L, double T, double R, double B)
{
    public double Area => Math.Max(0, R - L) * Math.Max(0, B - T);

    /// <summary>True if this rect's center lies inside <paramref name="outer"/> (geometric containment).</summary>
    public bool CenterIn(RectD outer)
    {
        double cx = (L + R) / 2.0;
        double cy = (T + B) / 2.0;
        return outer.L <= cx && cx <= outer.R && outer.T <= cy && cy <= outer.B;
    }
}

/// <summary>
/// Stateless, selector-independent UIA primitives. Every property read is wrapped
/// (the Python <c>_safe</c> pattern) because WebView2 elements go stale mid-walk and
/// throw <c>ElementNotAvailableException</c>/<c>COMException</c> on any access.
/// </summary>
internal static class Uia
{
    public static string Name(AutomationElement? e)
    {
        if (e is null) return "";
        try { return e.Properties.Name.ValueOrDefault ?? ""; }
        catch { return ""; }
    }

    public static string ClassNameLower(AutomationElement? e)
    {
        if (e is null) return "";
        try { return (e.Properties.ClassName.ValueOrDefault ?? "").ToLowerInvariant(); }
        catch { return ""; }
    }

    public static string AutomationId(AutomationElement? e)
    {
        if (e is null) return "";
        try { return e.Properties.AutomationId.ValueOrDefault ?? ""; }
        catch { return ""; }
    }

    public static bool IsText(AutomationElement? e)
    {
        if (e is null) return false;
        try { return e.Properties.ControlType.ValueOrDefault == ControlType.Text; }
        catch { return false; }
    }

    public static int ProcessId(AutomationElement? e)
    {
        if (e is null) return 0;
        try { return e.Properties.ProcessId.ValueOrDefault; }
        catch { return 0; }
    }

    /// <summary>Bounding rectangle, or null on failure / zero-area (offscreen).</summary>
    public static RectD? Rect(AutomationElement? e)
    {
        if (e is null) return null;
        try
        {
            var r = e.Properties.BoundingRectangle.ValueOrDefault;
            if (r.Width <= 0 && r.Height <= 0) return null;
            return new RectD(r.Left, r.Top, r.Right, r.Bottom);
        }
        catch { return null; }
    }

    public static double Area(AutomationElement? e) => Rect(e)?.Area ?? 0;

    /// <summary>
    /// Safe children fetch (empty on failure) via the injected <see cref="ITreeWalker"/>.
    /// The walker MUST be the RAW-view walker by default: Python's <c>uiautomation</c> lib
    /// sets <c>ViewWalker = IUIAutomation.RawViewWalker</c> (its ControlViewWalker line is
    /// commented out in the lib source). Walking the ControlView instead gives a pruned
    /// Chromium tree — participant tiles come back childless and speakers/roster are EMPTY
    /// (observed live on a real meeting, 2026-07-13). See TeamsSelectors.TreeView.
    /// </summary>
    public static AutomationElement[] Children(AutomationElement e, ITreeWalker walker)
    {
        var list = new List<AutomationElement>();
        try
        {
            var c = walker.GetFirstChild(e);
            int guard = 0;
            while (c is not null && guard++ < 20000)
            {
                list.Add(c);
                try { c = walker.GetNextSibling(c); }
                catch { break; } // element went stale mid-walk
            }
        }
        catch { /* stale/removed element — return what we have */ }
        return list.ToArray();
    }

    /// <summary>Safe control-view parent (null on failure/root).</summary>
    public static AutomationElement? Parent(AutomationElement? e, ITreeWalker walker)
    {
        if (e is null) return null;
        try { return walker.GetParent(e); }
        catch { return null; }
    }

    /// <summary>
    /// True when <paramref name="immediateParent"/> — or any of its control-view ancestors up to
    /// <paramref name="maxLevels"/> total — has a ClassName containing one of <paramref name="hints"/>.
    /// Python matches only the immediate parent; we climb a few levels because the control-view
    /// parent of a name TextControl is sometimes an inner wrapper (audit F1 resilience).
    /// </summary>
    public static bool AncestorClassMatches(AutomationElement? immediateParent, ITreeWalker walker,
        IReadOnlyList<string> hints, int maxLevels)
    {
        var cur = immediateParent;
        for (int lvl = 0; lvl < maxLevels && cur is not null; lvl++)
        {
            var pcls = ClassNameLower(cur);
            for (int h = 0; h < hints.Count; h++)
                if (hints[h].Length > 0 && pcls.Contains(hints[h])) return true;
            cur = Parent(cur, walker);
        }
        return false;
    }

    /// <summary>
    /// Iterative depth-bounded, node-capped DFS over the <b>control view</b>. Yields each element
    /// with its depth and its (control-view) parent — the parent is tracked during the walk, so
    /// the cheap immediate-parent read needs no extra COM call. Children are pushed last-first so
    /// pop order is natural document order.
    /// </summary>
    public static IEnumerable<(AutomationElement El, int Depth, AutomationElement? Parent)> Descendants(
        AutomationElement root, ITreeWalker walker, int maxDepth, int cap)
    {
        var stack = new Stack<(AutomationElement, int, AutomationElement?)>();
        stack.Push((root, 0, null));
        int count = 0;
        while (stack.Count > 0)
        {
            var (el, d, parent) = stack.Pop();
            yield return (el, d, parent);
            count++;
            if (count > cap || d >= maxDepth) continue;

            var children = Children(el, walker);
            for (int i = children.Length - 1; i >= 0; i--)
                stack.Push((children[i], d + 1, el));
        }
    }

    /// <summary>Fast native FindFirst for a descendant with the given AutomationId (null if none).</summary>
    public static AutomationElement? FindFirstByAutomationId(AutomationElement root, string automationId)
    {
        try { return root.FindFirstDescendant(cf => cf.ByAutomationId(automationId)); }
        catch { return null; }
    }
}
