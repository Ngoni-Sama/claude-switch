// Design kit: an Apple-like look. Soft gradient backgrounds, white (or
// graphite) cards with gentle shadows and generous radii, sentence-case titles,
// grey labels over values, gradient primary buttons and ring gauges (Ring.cs).
// Two palettes (Light default, Dark).
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
    public Color Card, Inner, Border, Track, AccentSoft, Accent2, BgTop, BgBottom, Sidebar, SidebarTop, SidebarBottom, NavText;
}

public static class Neu
{
    public static readonly Palette Light = new()
    {
        Name = "light", IsDark = false,
        Bg = C(0xEFEDF6), BgTop = C(0xEFEDF6), BgBottom = C(0xEFEDF6), Sidebar = C(0x4B33A8),
        SidebarTop = C(0x5A3FC0), SidebarBottom = C(0x2E1F6E), NavText = C(0xFFFFFF),
        Card = C(0xFFFFFF), Inner = C(0xF4F3F9), Border = C(0xE4E1EF), Track = C(0xE6E3F0),
        Hover = C(0xEAE7F4), Hi = C(0xFFFFFF), Lo = C(0xD8D4E6), Well = C(0xF4F3F9), Line = C(0xE6E3EE),
        Text = C(0x1E1B2E), Muted = C(0x5F5A72), Accent = C(0x5240CF), Accent2 = C(0x9A55E8), AccentSoft = C(0xECE8FB), OnAccent = C(0xFFFFFF),
        Good = C(0x1D7A45), Warn = C(0x9A5B00), Bad = C(0xC4161C),
    };

    public static readonly Palette Dark = new()
    {
        Name = "dark", IsDark = true,
        Bg = C(0x17151F), BgTop = C(0x17151F), BgBottom = C(0x17151F), Sidebar = C(0x241B47),
        SidebarTop = C(0x2C2157), SidebarBottom = C(0x120D26), NavText = C(0xF2EFFF),
        Card = C(0x221F2D), Inner = C(0x2D2A3A), Border = C(0x363246), Track = C(0x443F57),
        Hover = C(0x2F2B3D), Hi = C(0x2D2A3A), Lo = C(0x0D0C12), Well = C(0x2D2A3A), Line = C(0x363246),
        Text = C(0xF3F1FA), Muted = C(0xA7A2B8), Accent = C(0x8F80FF), Accent2 = C(0xC48BFF), AccentSoft = C(0x2E2752), OnAccent = C(0xFFFFFF),
        Good = C(0x4ADE80), Warn = C(0xFFB340), Bad = C(0xFF6B6B),
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

    public static BoxShadows RaisedShadow(double d) => default;
    public static BoxShadows InsetShadow(double d) => default;

    // A soft two-layer shadow for cards (none in dark mode, where it reads as dirt).
    public static BoxShadows CardShadow => P.IsDark ? default : BoxShadows.Parse("0 1 2 0 #0D000000, 0 8 24 0 #12000000");

    // The window and the sidebar: very gentle vertical gradients.
    // Solid, so the sidebar's open pill blends into the page exactly.
    public static IBrush PageBrush => B(P.Bg);
    public static IBrush SidebarBrush => Gradient(P.SidebarTop, P.SidebarBottom, vertical: true);

    // Small screens and windows that aren't maximised get tighter spacing.
    public static bool Dense { get; set; }
    public static IBrush AccentBrush => Gradient(P.Accent, P.Accent2);

    public static IBrush Gradient(Color a, Color b, bool vertical = false) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = vertical ? new RelativePoint(0, 1, RelativeUnit.Relative) : new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
    };

    public static Color UsageColor(int pct) => pct >= 95 ? P.Bad : pct >= 75 ? P.Warn : P.Accent;

    // ---- builders ----------------------------------------------------------------

    // A card. (Named for the old raised card; radius/depth are ignored.)
    public static Border Raised(Control child, double radius = 22, double depth = 8, Thickness? padding = null) => new()
    {
        Background = B(P.Card), BorderBrush = B(P.Border), BorderThickness = new Thickness(P.IsDark ? 1 : 0.6), CornerRadius = new CornerRadius(16),
        BoxShadow = CardShadow, Padding = padding ?? new Thickness(22, 20), Child = child,
    };

    public static Border Tile(Control child, Thickness? padding = null) =>
        Raised(child, 16, 0, padding ?? (Dense ? new Thickness(16, 14, 16, 12) : new Thickness(22, 20, 22, 16)));

    // The darker panel nested in a tile.
    public static Border Inset(Control child, double radius = 18, Thickness? padding = null) => new()
    {
        Background = B(P.Inner), CornerRadius = new CornerRadius(12),
        Padding = padding ?? new Thickness(18, 12), Child = child,
    };

    // Card and page titles, in sentence case.
    public static TextBlock Heading(string text, double size = 17) => new()
    {
        Text = text ?? "", FontSize = size + 1, FontWeight = FontWeight.SemiBold, Foreground = B(P.Text),
        TextWrapping = TextWrapping.Wrap, LetterSpacing = -0.2,
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
        s.Children.Add(new TextBlock { Text = number, FontSize = Dense ? 24 : 28, Foreground = B(color ?? P.Text), VerticalAlignment = VerticalAlignment.Bottom });
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
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(15, 7);
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
        if (primary)
        {
            Background = down ? Neu.Gradient(Neu.Mix(Neu.P.Accent, Neu.P.Text, 0.2), Neu.Mix(Neu.P.Accent2, Neu.P.Text, 0.2))
                       : IsPointerOver ? Neu.Gradient(Neu.Mix(Neu.P.Accent, Neu.P.Text, 0.08), Neu.Mix(Neu.P.Accent2, Neu.P.Text, 0.08))
                       : Neu.AccentBrush;
            BoxShadow = Neu.P.IsDark ? default : BoxShadows.Parse("0 2 8 0 #335240CF");
        }
        else
        {
            Background = Neu.B(selected ? Neu.P.AccentSoft : down ? Neu.Mix(Neu.P.Inner, Neu.P.Text, 0.1) : IsPointerOver ? Neu.P.Hover : Neu.P.Inner);
            BoxShadow = default;
        }
        label.Foreground = Neu.B(primary ? Neu.P.OnAccent : selected ? Neu.P.Accent : Neu.P.Text);
        bool ring = IsFocused && IsKeyboardFocusVisible();
        BorderBrush = ring ? Neu.B(Neu.P.Accent) : Brushes.Transparent;
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
