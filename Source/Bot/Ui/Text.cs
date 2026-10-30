/*
    nAlbum – telegram bot for sharing photos and videos in albums
    Copyright (C) 2026 Usevalad Buben usevaladbuben@gmail.com

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as published by
    the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
    or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

using System.Net;
using System.Globalization;

namespace nAlbum.Bot.Ui;

public static class Text
{
    public static string H(string s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>
    /// Invisible filler line (Braille blanks). Telegram shrinks inline buttons to the width of a short message,
    /// so view messages carry it to keep the buttons wide enough to tap.
    /// </summary>
    public static readonly string MinWidth = "\n" + new string('\u2800', 30);

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public static string CleanTitle(string s) =>
        Truncate(string.Join(' ', s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)), 100);

    public static int Int(string[] p, int i) => p.Length > i && int.TryParse(p[i], out var v) ? v : 0;

    public static long Long(string[] p, int i) => p.Length > i && long.TryParse(p[i], out var v) ? v : 0;

    public static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"First Last" of a Telegram user, or null.</summary>
    public static string UserName(Telegram.Bot.Types.User user)
    {
        if (user == null) return null;
        var name = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return name.Length == 0 ? null : Truncate(name, 64);
    }
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
