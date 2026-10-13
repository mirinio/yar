namespace Yar.Core;

/// <summary>A block of a treemap. <see cref="Index"/> is the item's position in the input, or -1 for the merged rest.</summary>
public readonly record struct TreemapRect(int Index, double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;

    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// Lays weights out as rectangles whose areas are in proportion to them, as close to square as
/// they will go (the "squarified" treemap of Bruls, Huizing and van Wijk). No drawing; only geometry.
/// </summary>
public static class Treemap
{
    /// <param name="minFraction">Items smaller than this share of the total are merged into one block at the end.</param>
    public static IReadOnlyList<TreemapRect> Layout(
        IReadOnlyList<double> weights, double width, double height, double minFraction = 0.004)
    {
        var total = weights.Where(w => w > 0).Sum();
        if (total <= 0 || width <= 0 || height <= 0)
            return [];

        var items = weights.Select((weight, index) => (Index: index, Weight: weight))
            .Where(item => item.Weight > 0)
            .OrderByDescending(item => item.Weight)
            .ToList();
        var big = items.Where(item => item.Weight / total >= minFraction).ToList();
        var rest = items.Skip(big.Count).Sum(item => item.Weight);
        // A lone small item is not worth hiding behind "others".
        if (items.Count - big.Count == 1)
        {
            big = items;
            rest = 0;
        }
        if (rest > 0)
            big.Add((-1, rest));
        big = [.. big.OrderByDescending(item => item.Weight)];

        var scale = width * height / total;
        var areas = big.Select(item => (item.Index, Area: item.Weight * scale)).ToList();

        var result = new List<TreemapRect>(areas.Count);
        double x = 0, y = 0, w = width, h = height;
        var row = new List<(int Index, double Area)>();
        var position = 0;
        while (position < areas.Count)
        {
            var side = Math.Min(w, h);
            var next = areas[position];
            // Keep adding to the current row while that makes its blocks more square, not less.
            if (row.Count == 0 || Worst(row, side) >= Worst([.. row, next], side))
            {
                row.Add(next);
                position++;
                continue;
            }
            Place(row, ref x, ref y, ref w, ref h, result);
            row.Clear();
        }
        if (row.Count > 0)
            Place(row, ref x, ref y, ref w, ref h, result);
        return result;
    }

    // The worst (largest) aspect ratio a row's blocks would have along a side of this length.
    private static double Worst(List<(int Index, double Area)> row, double side)
    {
        var sum = row.Sum(item => item.Area);
        var max = row.Max(item => item.Area);
        var min = row.Min(item => item.Area);
        var sideSquared = side * side;
        return Math.Max(sideSquared * max / (sum * sum), sum * sum / (sideSquared * min));
    }

    // Lays a finished row along the shorter side of the free space, then shrinks the free space by it.
    private static void Place(
        List<(int Index, double Area)> row, ref double x, ref double y, ref double w, ref double h,
        List<TreemapRect> result)
    {
        var sum = row.Sum(item => item.Area);
        if (w >= h)
        {
            var thickness = h > 0 ? sum / h : 0;
            var offset = y;
            foreach (var (index, area) in row)
            {
                var length = thickness > 0 ? area / thickness : 0;
                result.Add(new(index, x, offset, thickness, length));
                offset += length;
            }
            x += thickness;
            w -= thickness;
        }
        else
        {
            var thickness = w > 0 ? sum / w : 0;
            var offset = x;
            foreach (var (index, area) in row)
            {
                var length = thickness > 0 ? area / thickness : 0;
                result.Add(new(index, offset, y, length, thickness));
                offset += length;
            }
            y += thickness;
            h -= thickness;
        }
    }
}
