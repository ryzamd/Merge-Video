// ---------------- NumericNameComparer ----------------
using MergeVideo;

internal class NumericNameComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        // Extract numeric prefix if present
        int nx = Program.NumericPrefixOrDefault(Path.GetFileName(x)!);
        int ny = Program.NumericPrefixOrDefault(Path.GetFileName(y)!);

        int cmp = nx.CompareTo(ny);
        if (cmp != 0) return cmp;

        // Fallback to normal string comparison
        return StringComparer.CurrentCultureIgnoreCase.Compare(x, y);
    }
}
