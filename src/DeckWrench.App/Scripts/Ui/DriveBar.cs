using DeckWrench.Core.Storage;
using DeckWrench.Host.Toolbox;
using Godot;

namespace DeckWrench.App.Ui;

/// <summary>
/// A drive's space as one stacked bar (games, prefixes, shader caches, Proton, mods and backups,
/// everything else, free) with a legend. Used by both UI modes.
/// </summary>
public partial class DriveBar : VBoxContainer
{
    private readonly DriveReport _report;
    private readonly bool _compact;

    public DriveBar(DriveReport report, bool compact)
    {
        _report = report;
        _compact = compact;
        AddThemeConstantOverride("separation", compact ? 4 : 8);
        var d = report.Drive;
        var head = new HBoxContainer();
        var size = compact ? 14 : UiTheme.FontSize;
        var name = UiTheme.Label($"{d.Label}", size, UiTheme.Text);
        head.AddChild(name);
        var mount = UiTheme.Label($"  {d.Mount}", compact ? 12 : UiTheme.SmallSize, UiTheme.Dim);
        mount.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        mount.VerticalAlignment = VerticalAlignment.Center;
        head.AddChild(mount);
        head.AddChild(UiTheme.Label($"{DiskSize.Format(d.Free)} free of {DiskSize.Format(d.Total)}", compact ? 12 : UiTheme.SmallSize + 1,
            d.Total > 0 && d.Free < d.Total / 10 ? UiTheme.Warn : UiTheme.Dim));
        AddChild(head);
        var bar = new Control { CustomMinimumSize = new Vector2(0, compact ? 12 : 18), MouseFilter = MouseFilterEnum.Ignore };
        bar.Draw += () => DrawBar(bar);
        AddChild(bar);
        var legend = new HFlowContainer();
        legend.AddThemeConstantOverride("h_separation", compact ? 14 : 22);
        foreach (var ((label, bytes), color) in report.Breakdown.Zip(Colors()))
        {
            if (bytes <= 0)
            {
                continue;
            }
            var item = new HBoxContainer();
            item.AddThemeConstantOverride("separation", 6);
            item.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(10, 10), SizeFlagsVertical = SizeFlags.ShrinkCenter });
            item.AddChild(UiTheme.Label($"{label} {DiskSize.Format(bytes)}", compact ? 12 : UiTheme.SmallSize, UiTheme.Dim));
            legend.AddChild(item);
        }
        AddChild(legend);
    }

    /// <summary>One colour per breakdown part, in order; free space is the bar's background.</summary>
    public static Color[] Colors() =>
    [
        UiTheme.Accent,
        UiTheme.Info,
        UiTheme.Good,
        new Color("a78bfa"),  // violet: stays apart from amber accents and warnings
        UiTheme.Bad.Lerp(UiTheme.Accent, 0.4f),
        UiTheme.Dim.Darkened(0.2f),
    ];

    private void DrawBar(Control bar)
    {
        var total = Math.Max(1, _report.Drive.Total);
        var r = new Rect2(Vector2.Zero, bar.Size);
        bar.DrawRect(r, UiTheme.PanelHi);
        var x = 0f;
        foreach (var ((_, bytes), color) in _report.Breakdown.Zip(Colors()))
        {
            var w = (float)(bar.Size.X * (double)bytes / total);
            if (w <= 0)
            {
                continue;
            }
            bar.DrawRect(new Rect2(x, 0, w, bar.Size.Y), color);
            x += w;
        }
        bar.DrawRect(r, UiTheme.Line, filled: false, width: 1);
    }
}
