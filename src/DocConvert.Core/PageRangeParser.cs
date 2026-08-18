namespace DocConvert.Core;

public static class PageRangeParser
{
    public static IReadOnlyList<int> Parse(string? value, int pageCount)
    {
        if (pageCount < 1) return [];
        if (string.IsNullOrWhiteSpace(value)) return [];

        var pages = new SortedSet<int>();
        foreach (var segment in value.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = segment.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length == 1)
            {
                pages.Add(ToIndex(bounds[0], pageCount));
                continue;
            }

            if (bounds.Length != 2)
                throw new FormatException($"页码范围“{segment}”格式不正确。请使用 1,3-5。 ");

            var start = ToIndex(bounds[0], pageCount);
            var end = ToIndex(bounds[1], pageCount);
            if (start > end) (start, end) = (end, start);
            for (var page = start; page <= end; page++) pages.Add(page);
        }

        return pages.ToArray();
    }

    public static IReadOnlyList<WatermarkRegion> ApplyScope(
        IEnumerable<WatermarkRegion> source,
        WatermarkScope scope,
        string? pageRange,
        int currentPageIndex,
        int pageCount)
    {
        var regions = source.ToArray();
        if (regions.Length == 0) return [];

        if (scope == WatermarkScope.CurrentPage)
        {
            var page = Math.Clamp(currentPageIndex, 0, Math.Max(0, pageCount - 1));
            return regions.Where(region => region.PageIndex == page).Distinct().ToArray();
        }

        var targetPages = scope switch
        {
            WatermarkScope.PageRange => Parse(pageRange, pageCount),
            _ => Enumerable.Range(0, Math.Max(1, pageCount)).ToArray()
        };

        if (scope == WatermarkScope.PageRange && targetPages.Count == 0)
            throw new FormatException("请输入页码范围，例如 1,3-5。");

        return regions
            .SelectMany(region => targetPages.Select(page => region with { PageIndex = page }))
            .Distinct()
            .ToArray();
    }

    private static int ToIndex(string value, int pageCount)
    {
        if (!int.TryParse(value, out var page) || page < 1 || page > pageCount)
            throw new FormatException($"页码“{value}”无效，有效范围是 1-{pageCount}。");
        return page - 1;
    }
}
