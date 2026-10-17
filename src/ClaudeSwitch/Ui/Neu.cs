// Design kit, after pc_info: flat tiles with a thin border and a small radius,
// bold uppercase tile titles, small grey labels over larger values, nested
// inner panels, thin bordered bars and ring gauges (Ring.cs). Two palettes
// (Light default, Dark).
//
// Contrast (WCAG, on Card and Inner): every text colour passes AA 4.5:1.
// State is never shown by colour alone: dots and rings carry a text label.

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public class Palette
{
    public string Name;
    public bool IsDark;
    public Color Bg, Hover, Hi, Lo, Well, Line, Text, Muted, Accent, OnAccent, Good, Warn, Bad;
    public Color Card, Inner, Border, Track, AccentSoft;
}

public static class Neu
{
    public static readonly Palette Light = new()
    {
        Name = "light", IsDark = false,
        Bg = C(0xF7F8FA), Card = C(0xEFF0F3), Inner = C(0xE5E7EB), Border = C(0xDADDE2), Track = C(0xD2D6DC),
        Hover = C(0xE6E8EC), Hi = C(0xFFFFFF), Lo = C(0xDADDE2), Well = C(0xE5E7EB), Line = C(0xD5D9DF),
        Text = C(0x1B1F24), Muted = C(0x555D6A), Accent = C(0x0A62A0), AccentSoft = C(0xDCE9F4), OnAccent = C(0xFFFFFF),
        Good = C(0x0B6B1F), Warn = C(0x825400), Bad = C(0xB0261F),
    };

    public static readonly Palette Dark = new()
    {
        Name = "dark", IsDark = true,
        Bg = C(0x17181B), Card = C(0x212327), Inner = C(0x2A2D32), Border = C(0x34373D), Track = C(0x3B3F46),
        Hover = C(0x2D3036), Hi = C(0x2A2D32), Lo = C(0x0F1012), Well = C(0x2A2D32), Line = C(0x363940),
        Text = C(0xECEEF1), Muted = C(0xA9AFB9), Accent = C(0x4DB0F2), AccentSoft = C(0x1D3447), OnAccent = C(0x0A1520),
        Good = C(0x5BD27A), Warn = C(0xF2C14E), Bad = C(0xFF7B76),
    };

    public static Palette P { get; private set; } = Light;
    public static event Action ThemeChanged;

    public static void Use(string name)
    {
        P = name == "dark" ? Dark : Light;
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = P.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
            Application.Current.Resources["SystemAccentColor"] = P.Accent;
            Application.Current.Resources["SystemAccentColorLight1"] = P.Accent;
            Application.Current.Resources["SystemAccentColorDark1"] = P.Accent;
        }
    }

    public static void Switch(string name)
    {
        Use(name);
        Settings.Theme = P.Name;
        ThemeChanged?.Invoke();
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    public static IBrush B(Color c) => new SolidColorBrush(c);
    static string Hex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
    static string Hex(Color c, byte alpha) => "#" + alpha.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    // Flat look: no shadows any more. Kept for callers.
    public static BoxShadows RaisedShadow(double d) => default;
    public static BoxShadows InsetShadow(double d) => default;

    public static Color UsageColor(int pct) => pct >= 95 ? P.Bad : pct >= 75 ? P.Warn : P.Accent;

    // ---- builders ----------------------------------------------------------------

    // A tile. (Named for the old raised card; radius/depth are ignored now.)
    public static Border Raised(Control child, double radius = 22, double depth = 8, Thickness? padding = null) => new()
    {
        Background = B(P.Card), BorderBrush = B(P.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(22, 20), Child = child,
    };

    public static Border Tile(Control child, Thickness? padding = null) => Raised(child, 8, 0, padding ?? new Thickness(20, 18, 20, 14));

    // The darker panel nested in a tile.
    public static Border Inset(Control child, double radius = 18, Thickness? padding = null) => new()
    {
        Background = B(P.Inner), CornerRadius = new CornerRadius(6),
        Padding = padding ?? new Thickness(18, 12), Child = child,
    };

    // "PC DETAILS"-style tile title.
    public static TextBlock Heading(string text, double size = 17) => new()
    {
        Text = (text ?? "").ToUpperInvariant(), FontSize = size, FontWeight = FontWeight.Bold, Foreground = B(P.Text),
        TextWrapping = TextWrapping.Wrap,
    };

    // Small grey label over a larger value.
    public static StackPanel Field(string label, string value, Color? color = null, double size = 15, string tip = null)
    {
        var s = new StackPanel { Spacing = 1 };
        s.Children.Add(T(label, 12, P.Muted));
        var v = T(value, size, color);
        if (tip != null) ToolTip.SetTip(v, tip);
        s.Children.Add(v);
        return s;
    }

    // Big number + small unit word: "3 profiles".
    public static Control Stat(string number, string unit, Color? color = null)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        s.Children.Add(new TextBlock { Text = number, FontSize = 28, Foreground = B(color ?? P.Text), VerticalAlignment = VerticalAlignment.Bottom });
        s.Children.Add(new TextBlock { Text = unit, FontSize = 13.5, Foreground = B(P.Text), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) });
        AutomationProperties.SetName(s, number + " " + unit);
        return s;
    }

    public static TextBlock T(string text, double size = 14, Color? color = null, FontWeight weight = FontWeight.Normal, bool wrap = false) => new()
    {
        Text = text ?? "", FontSize = size, Foreground = B(color ?? P.Text), FontWeight = weight,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        LineHeight = wrap ? size * 1.45 : double.NaN,
    };

    public static TextBlock Para(string text, double size = 13.5, Color? color = null) => T(text, size, color ?? P.Muted, FontWeight.Normal, wrap: true);

    public static TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = B(P.Muted),
        LetterSpacing = 0.6, Margin = new Thickness(0, 16, 0, 8),
    };

    public static Control Dot(Color c, string label)
    {
        var e = new Ellipse { Width = 10, Height = 10, Fill = B(c), VerticalAlignment = VerticalAlignment.Center };
        var host = new Border
        {
            Child = e, Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(host, label);
        ToolTip.SetTip(host, label);
        return host;
    }

    // Thin bordered track with a coloured fill (pc_info's power bar). pct < 0 = unknown.
    public static Control Bar(int pct, double height = 10)
    {
        var track = new Grid { Height = height };
        track.Children.Add(new Border { BorderBrush = B(P.Track), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(height / 2) });
        if (pct > 0)
        {
            double frac = Math.Min(100, pct) / 100.0;
            var fill = new Border { Background = B(UsageColor(pct)), CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2) };
            track.Children.Add(fill);
            track.SizeChanged += (_, e) => fill.Width = Math.Max(height - 4, (e.NewSize.Width - 4) * frac);
        }
        AutomationProperties.SetName(track, pct < 0 ? "no reading" : pct + " percent used");
        return track;
    }

    public static Border Line() => new() { Height = 1, Background = B(P.Line), Margin = new Thickness(0, 4) };
}

// Flat bordered button. Enter/Space activate it; keyboard focus shows an
// accent ring. Selected = toggled on (tabs / modes).
public class NeuButton : Border
{
    public event Action Click;
    readonly TextBlock label;
    readonly bool primary;
    bool down, selected;

    public NeuButton(string text, bool primary = false, double fontSize = 13.5)
    {
        this.primary = primary;
        Focusable = true;
        IsTabStop = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(14, 7);
        MinHeight = 34;
        BorderThickness = new Thickness(1);
        label = Neu.T(text, fontSize, null, FontWeight.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        Child = label;
        AutomationProperties.SetName(this, text);
        Paint();

        PointerPressed += (_, e) => { if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; Focus(); down = true; e.Pointer.Capture(this); Paint(); e.Handled = true; };
        PointerReleased += (_, e) =>
        {
            if (!down) return;
            down = false;
            e.Pointer.Capture(null);
            Paint();
            if (new Rect(Bounds.Size).Contains(e.GetPosition(this))) Fire();
            e.Handled = true;
        };
        PointerCaptureLost += (_, _) => { down = false; Paint(); };
        PointerEntered += (_, _) => Paint();
        PointerExited += (_, _) => Paint();
        GotFocus += (_, _) => Paint();
        LostFocus += (_, _) => Paint();
        KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Fire(); e.Handled = true; } };
        PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) { Opacity = IsEnabled ? 1 : 0.45; } };
    }

    public string Text { get => label.Text; set { label.Text = value; AutomationProperties.SetName(this, value); } }

    // A selected button stays pressed in (tabs / toggles).
    public bool Selected { get => selected; set { selected = value; Paint(); } }

    void Paint()
    {
        Color bg = primary ? (down ? Neu.Mix(Neu.P.Accent, Neu.P.Text, 0.15) : IsPointerOver ? Neu.Mix(Neu.P.Accent, Neu.P.Text, 0.08) : Neu.P.Accent)
                 : selected ? Neu.P.AccentSoft : down ? Neu.P.Inner : IsPointerOver ? Neu.P.Hover : Neu.P.Card;
        Background = Neu.B(bg);
        label.Foreground = Neu.B(primary ? Neu.P.OnAccent : selected ? Neu.P.Accent : Neu.P.Text);
        bool ring = IsFocused && IsKeyboardFocusVisible();
        BorderBrush = Neu.B(ring ? Neu.P.Text : primary || selected ? Neu.P.Accent : Neu.P.Border);
    }

    bool IsKeyboardFocusVisible() => IsFocused;

    void Fire() { if (IsEnabled) Click?.Invoke(); }
}

// pc_info's underline tab ("CPU 0"): text with an accent bar under the
// selected one.
public class UnderlineTab : StackPanel
{
    public event Action Click;

    public UnderlineTab(string text, bool selected)
    {
        Focusable = true;
        IsTabStop = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        Spacing = 5;
        Margin = new Thickness(0, 0, 22, 0);
        Background = Brushes.Transparent;   // whole area clickable
        var t = Neu.T(text, 14, selected ? Neu.P.Text : Neu.P.Muted, selected ? FontWeight.SemiBold : FontWeight.Normal);
        t.Margin = new Thickness(2, 0);
        Children.Add(t);
        Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = selected ? Neu.B(Neu.P.Accent) : Brushes.Transparent });
        AutomationProperties.SetName(this, text + (selected ? ", selected" : ""));
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { Click?.Invoke(); e.Handled = true; } };
        PointerEntered += (_, _) => { if (!selected) t.Foreground = Neu.B(Neu.P.Text); };
        PointerExited += (_, _) => { if (!selected) t.Foreground = Neu.B(Neu.P.Muted); };
        KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Click?.Invoke(); e.Handled = true; } };
    }
}
