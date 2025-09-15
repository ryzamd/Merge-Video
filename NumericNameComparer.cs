using System.Text.RegularExpressions;

internal class NumericNameComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        // Extract numeric prefix if present
        int nx = NumericPrefixOrDefault(Path.GetFileName(x)!);
        int ny = NumericPrefixOrDefault(Path.GetFileName(y)!);

        int cmp = nx.CompareTo(ny);
        if (cmp != 0) return cmp;

        // Fallback to normal string comparison
        return StringComparer.CurrentCultureIgnoreCase.Compare(x, y);
    }

    private static int ExtractNumericPrefix(string name)
    {
        var match = Regex.Match(name, @"^\s*(?<n>\d+)");
        if (match.Success && int.TryParse(match.Groups["n"].Value, out int n))
            return n;
        return int.MaxValue; // Non-numeric names go to the end
    }

    // Static helper method for convenience
    public static int NumericPrefixOrDefault(string name)
    {
        var match = Regex.Match(name, @"^\s*(?<n>\d+)");
        if (match.Success && int.TryParse(match.Groups["n"].Value, out int n))
            return n;
        return int.MaxValue;
    }
}
