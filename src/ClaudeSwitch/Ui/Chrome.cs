// The window's own title bar: minimise, maximise/restore and close, drawn to
// match the rest of the app instead of the system's bar.
//
// Windows and Linux get our buttons. macOS keeps its traffic lights, because
// that's what Mac users reach for and where they expect them; we only leave
// room for them and draw the title alongside.
//
// The client area is extended rather than the decorations removed, so the
// window keeps its native resize edges, snapping and shadow.

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public static class Chrome
{
    public const double BarHeight = 44;
    // Width of the macOS traffic lights, so the title clears them.
    const double MacButtonsWidth = 78;

    public static void Apply(Window w)
    {
        w.ExtendClientAreaToDecorationsHint = true;
        // 0, not -1: -1 reserves a system caption strip, which then draws its
        // own title on top of ours and shows through as a pale band.
        w.ExtendClientAreaTitleBarHeightHint = 0;
        // macOS: keep the real traffic lights. Elsewhere: we draw everything.
        w.ExtendClientAreaChromeHints = Platform.IsMac
            ? ExtendClientAreaChromeHints.PreferSystemChrome
            : ExtendClientAreaChromeHints.NoChrome;
        w.SystemDecorations = SystemDecorations.Full;   // keeps resize, snap and shadow
    }

    /// <summary>The bar itself. Drag to move, double-click to maximise.</summary>
    public static Control Bar(Window w, string title, bool canMaximize)
    {
        var bar = new DockPanel();

        if (!Platform.IsMac)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            if (w.CanResize || canMaximize)
            {
                buttons.Children.Add(Button("Minimise", Minimise(), () => w.WindowState = WindowState.Minimized));
                if (canMaximize)
                {
                    WindowButton max = null;
                    max = Button(MaxLabel(w), MaxGlyph(w), () =>
                    {
                        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    });
                    // Keep the glyph in step with the state (snapping changes it too).
                    w.PropertyChanged += (_, e) =>
                    {
                        if (e.Property != Window.WindowStateProperty || max == null) return;
                        max.SetGlyph(MaxGlyph(w));
                        AutomationProperties.SetName(max, MaxLabel(w));
                        ToolTip.SetTip(max, MaxLabel(w));
                    };
                    buttons.Children.Add(max);
                }
            }
            else
            {
                buttons.Children.Add(Button("Minimise", Minimise(), () => w.WindowState = WindowState.Minimized));
            }
            buttons.Children.Add(Button("Close", Close(), w.Close, danger: true));
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
        }

        var heading = Neu.T(title, 13, Neu.P.Muted, FontWeight.SemiBold);
        heading.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(heading);

        // Painted edge to edge: the extended client area has nothing behind it,
        // so an unpainted strip reads as a pale band above the page.
        var host = new Border
        {
            Height = BarHeight,
            Background = Neu.B(Neu.P.Bg),
            Padding = new Thickness(Platform.IsMac ? MacButtonsWidth : 16, 0, Platform.IsMac ? 16 : 10, 0),
            Child = bar,
        };
        host.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(host).Properties.IsLeftButtonPressed) w.BeginMoveDrag(e);
        };
        if (canMaximize)
        {
            host.DoubleTapped += (_, _) =>
                w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        return host;
    }

    /// <summary>
    /// Puts the bar above <paramref name="body"/> and returns the whole thing.
    /// Dialogs that size to their content pass <paramref name="fitContent"/>,
    /// so the body row asks for its natural height instead of filling.
    /// </summary>
    public static Control Wrap(Window w, string title, Control body, bool canMaximize, bool fitContent = false)
    {
        Apply(w);
        var root = new Grid { RowDefinitions = new RowDefinitions(fitContent ? "Auto,Auto" : "Auto,*") };
        root.Children.Add(Bar(w, title, canMaximize));
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    static string MaxLabel(Window w) => w.WindowState == WindowState.Maximized ? "Restore down" : "Maximise";

    // Glyphs are drawn, not typed: no icon font to depend on, and they stay
    // crisp at any scale.
    static Geometry Minimise() => Geometry.Parse("M 0,5 H 10");
    static Geometry Close() => Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10");

    static Geometry MaxGlyph(Window w) => w.WindowState == WindowState.Maximized
        ? Geometry.Parse("M 0,2.5 H 7.5 V 10 H 0 Z M 2.5,2.5 V 0 H 10 V 7.5 H 7.5")   // two overlapped squares
        : Geometry.Parse("M 0,0 H 10 V 10 H 0 Z");

    static WindowButton Button(string label, Geometry glyph, Action click, bool danger = false)
    {
        var b = new WindowButton(glyph, danger);
        AutomationProperties.SetName(b, label);
        ToolTip.SetTip(b, label);
        b.Click += click;
        return b;
    }
}

// A compact title-bar button: raised like the rest of the kit, pressing in on
// click, with the close button turning red on hover.
public class WindowButton : Border
{
    public event Action Click;
    readonly Avalonia.Controls.Shapes.Path icon;
    readonly bool danger;
    bool down;

    public WindowButton(Geometry glyph, bool danger)
    {
        this.danger = danger;
        Width = 38;
        Height = 30;
        CornerRadius = new CornerRadius(9);
        Focusable = true;
        IsTabStop = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        BorderThickness = new Thickness(1.5);
        icon = new Avalonia.Controls.Shapes.Path
        {
            Data = glyph, StrokeThickness = 1.4, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Child = icon;
        Paint();

        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { down = true; e.Pointer.Capture(this); Paint(); e.Handled = true; } };
        PointerReleased += (_, e) =>
        {
            if (!down) return;
            down = false;
            e.Pointer.Capture(null);
            Paint();
            if (new Rect(Bounds.Size).Contains(e.GetPosition(this))) Click?.Invoke();
            e.Handled = true;
        };
        PointerCaptureLost += (_, _) => { down = false; Paint(); };
        PointerEntered += (_, _) => Paint();
        PointerExited += (_, _) => Paint();
        GotFocus += (_, _) => Paint();
        LostFocus += (_, _) => Paint();
        KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Click?.Invoke(); e.Handled = true; } };
    }

    public void SetGlyph(Geometry glyph) => icon.Data = glyph;

    void Paint()
    {
        bool hot = danger && (IsPointerOver || down);
        Background = Neu.B(hot ? Neu.P.Bad : down ? Neu.P.Inner : IsPointerOver ? Neu.P.Hover : Neu.P.Bg);
        icon.Stroke = Neu.B(hot ? Neu.P.OnAccent : IsPointerOver ? Neu.P.Text : Neu.P.Muted);
        BorderBrush = IsFocused ? Neu.B(Neu.P.Accent) : Brushes.Transparent;
    }
}
