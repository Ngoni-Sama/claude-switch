// Thin ring gauge (after pc_info's battery-health ring): a faint full track,
// the used part drawn clockwise from the top, a dot where it ends, and the
// percentage in the middle.

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClaudeSwitch.Ui;

public class Ring : Control
{
    readonly int pct;          // -1 = no reading
    readonly double size, stroke;
    readonly Color color;
    readonly string caption;

    public Ring(int pct, double size = 96, double stroke = 5, Color? color = null, string caption = null)
    {
        this.pct = pct;
        this.size = size;
        this.stroke = stroke;
        this.color = color ?? Neu.UsageColor(Math.Max(0, pct));
        this.caption = caption;
        Width = size;
        Height = size;
        AutomationProperties.SetName(this, pct < 0 ? "no reading" : pct + " percent used");
    }

    public override void Render(DrawingContext ctx)
    {
        double r = (size - stroke) / 2 - 3;   // room for the end dot
        var c = new Point(size / 2, size / 2);
        ctx.DrawEllipse(null, new Pen(Neu.B(Neu.P.Track), stroke), c, r, r);

        if (pct > 0)
        {
            double frac = Math.Min(100, pct) / 100.0;
            // Accent rings fade blue to indigo; warning and limit rings brighten along the arc.
            Color end2 = color == Neu.P.Accent ? Neu.P.Accent2 : Neu.Mix(color, Colors.White, Neu.P.IsDark ? 0.15 : 0.3);
            var pen = new Pen(Neu.Gradient(color, end2), stroke, lineCap: PenLineCap.Round);
            if (frac >= 0.999) ctx.DrawEllipse(null, pen, c, r, r);
            else
            {
                double end = -Math.PI / 2 + frac * 2 * Math.PI;
                var start = new Point(c.X, c.Y - r);
                var stop = new Point(c.X + r * Math.Cos(end), c.Y + r * Math.Sin(end));
                var g = new StreamGeometry();
                using (StreamGeometryContext s = g.Open())
                {
                    s.BeginFigure(start, false);
                    s.ArcTo(stop, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise);
                    s.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, g);
                ctx.DrawEllipse(Neu.B(end2), null, stop, stroke * 0.95, stroke * 0.95);
            }
        }

        string text = pct < 0 ? "–" : pct + "%";
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), size * 0.22, Neu.B(pct < 0 ? Neu.P.Muted : color));
        double y = c.Y - ft.Height / 2 - (caption != null ? size * 0.06 : 0);
        ctx.DrawText(ft, new Point(c.X - ft.Width / 2, y));
        if (caption != null)
        {
            var cap = new FormattedText(caption.ToUpperInvariant(), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default), Math.Max(8.5, size * 0.095), Neu.B(Neu.P.Muted));
            ctx.DrawText(cap, new Point(c.X - cap.Width / 2, y + ft.Height - 1));
        }
    }
}
