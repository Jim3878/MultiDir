namespace MultiDir.Services;

/// <summary>
/// Tab labels that stay unique when two open folders share a name: "專案A\src", or "專案A\…\src" when the
/// first differing ancestor is further up. Tabs without a clash keep their plain name.
/// </summary>
public static class TabTitles
{
    /// <param name="tabs">(full path, plain title) of every open tab.</param>
    /// <returns>The label for each tab, in the same order.</returns>
    public static string[] Compute(IReadOnlyList<(string Path, string Title)> tabs)
    {
        var result = tabs.Select(t => t.Title).ToArray();
        var groups = Enumerable.Range(0, tabs.Count).GroupBy(i => tabs[i].Title, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            // The same folder open twice is not a clash worth labeling.
            var distinct = group.Select(i => Normalize(tabs[i].Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count < 2) continue;
            var segs = distinct.ToDictionary(p => p, Segments, StringComparer.OrdinalIgnoreCase);
            foreach (var i in group)
            {
                var mine = segs[Normalize(tabs[i].Path)];
                result[i] = Normalize(tabs[i].Path); // no single ancestor tells it apart (rare): full path
                var others = segs.Values.Where(s => s != mine).ToList();
                // Walk up until this ancestor tells it apart from every other same-named tab.
                for (int k = 1; k < mine.Length; k++)
                {
                    var ancestor = mine[^(k + 1)];
                    if (others.Any(o => o.Length > k && string.Equals(o[^(k + 1)], ancestor, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    result[i] = k == 1 ? $"{ancestor}\\{tabs[i].Title}" : $"{ancestor}\\…\\{tabs[i].Title}";
                    break;
                }
            }
        }
        return result;
    }

    private static string Normalize(string path) => path.TrimEnd('\\');

    private static string[] Segments(string path) => path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
}
