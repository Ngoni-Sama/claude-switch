// Column layout that always fills the width: as many columns as fit at
// MinColumnWidth, stretched to share the space.
//   Rows = false: each tile drops into the shortest column (pc_info's
//                 masonry), so tiles of different heights leave no holes.
//   Rows = true:  tiles go left to right and every tile in a row is as tall
//                 as the tallest one (profile tiles line up).

using Avalonia;
using Avalonia.Controls;

namespace ClaudeSwitch.Ui;

public class Masonry : Panel
{
    public double MinColumnWidth { get; set; } = 330;
    public double Spacing { get; set; } = 16;
    public int MaxColumns { get; set; } = 6;
    public bool Rows { get; set; }

    readonly List<Rect> slots = new();
    double laidOutWidth = -1;

    int Columns(double width)
    {
        int visible = Children.Count(c => c.IsVisible);
        int n = (int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing));
        return Math.Max(1, Math.Min(Math.Min(n, MaxColumns), Math.Max(1, visible)));
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinColumnWidth * 2 + Spacing : available.Width;
        int cols = Columns(width);
        double colW = (width - Spacing * (cols - 1)) / cols;
        slots.Clear();
        laidOutWidth = width;

        if (Rows)
        {
            var visible = Children.Where(c => c.IsVisible).ToList();
            double y = 0;
            for (int i = 0; i < visible.Count; i += cols)
            {
                var row = visible.Skip(i).Take(cols).ToList();
                double h = 0;
                foreach (Control c in row) { c.Measure(new Size(colW, double.PositiveInfinity)); h = Math.Max(h, c.DesiredSize.Height); }
                for (int k = 0; k < row.Count; k++) slots.Add(new Rect(k * (colW + Spacing), y, colW, h));
                y += h + Spacing;
            }
            return new Size(width, Math.Max(0, y - Spacing));
        }

        var heights = new double[cols];
        foreach (Control c in Children)
        {
            if (!c.IsVisible) { slots.Add(default); continue; }
            c.Measure(new Size(colW, double.PositiveInfinity));
            int k = Array.IndexOf(heights, heights.Min());
            slots.Add(new Rect(k * (colW + Spacing), heights[k], colW, c.DesiredSize.Height));
            heights[k] += c.DesiredSize.Height + Spacing;
        }
        return new Size(width, Math.Max(0, heights.Max() - Spacing));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Math.Abs(final.Width - laidOutWidth) > 0.5) MeasureOverride(final);
        var items = Rows ? Children.Where(c => c.IsVisible).ToList() : Children.ToList();
        for (int i = 0; i < items.Count && i < slots.Count; i++) items[i].Arrange(slots[i]);
        return final;
    }
}
