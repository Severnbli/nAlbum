using System.Net;
using System.Globalization;

namespace nAlbum.Bot.Ui;

public static class Text
{
    public static string H(string s) => WebUtility.HtmlEncode(s ?? "");

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public static string CleanTitle(string s) =>
        Truncate(string.Join(' ', s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)), 100);

    public static int Int(string[] p, int i) => p.Length > i && int.TryParse(p[i], out var v) ? v : 0;

    public static long Long(string[] p, int i) => p.Length > i && long.TryParse(p[i], out var v) ? v : 0;

    public static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
}

public static class NumberParser
{
    /// <summary>"12", "12 15 18", "12-15", "3, 7-9" -> set of numbers; null if unreadable.</summary>
    public static SortedSet<int> Parse(string text)
    {
        var set = new SortedSet<int>();
        foreach (var token in text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('-');
            if (parts.Length == 1 && int.TryParse(parts[0], out var one))
            {
                set.Add(one);
            }
            else if (parts.Length == 2 && int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi)
                     && lo <= hi && hi - lo < 100)
            {
                for (var n = lo; n <= hi; n++) set.Add(n);
            }
            else
            {
                return null;
            }

            if (set.Count > 100) return null;
        }

        return set;
    }
}
