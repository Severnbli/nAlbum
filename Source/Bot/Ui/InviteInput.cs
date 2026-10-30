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

using System.Text.RegularExpressions;

namespace nAlbum.Bot.Ui;

/// <summary>Reads the lifetime and the use limit of an invite link typed by the user.</summary>
public static partial class InviteInput
{
    public const long MaxSeconds = 365L * 24 * 3600;
    public const int MaxUses = 10_000;

    // "1d 10h 30m 17s", "90m", "1d10h"; the same units in Russian and Belarusian letters
    [GeneratedRegex(@"^\s*(?:\d{1,9}\s*[a-zа-яё]\s*)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WholePattern();

    [GeneratedRegex(@"(\d{1,9})\s*([a-zа-яё])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartPattern();

    /// <summary>Whole seconds, or null when the text is not a duration from 1 second to a year.</summary>
    public static long? ParseDuration(string text)
    {
        if (text == null || text.Length > 64 || !WholePattern().IsMatch(text)) return null;

        var seen = new HashSet<long>();
        long total = 0;
        foreach (Match part in PartPattern().Matches(text))
        {
            var unit = char.ToLowerInvariant(part.Groups[2].Value[0]) switch
            {
                'd' or 'д' => 86400L,
                'h' or 'ч' or 'г' => 3600L,
                'm' or 'м' or 'х' => 60L,
                's' or 'с' => 1L,
                _ => 0L
            };
            if (unit == 0 || !seen.Add(unit)) return null; // unknown or repeated unit

            total += long.Parse(part.Groups[1].Value) * unit; // at most 9 digits: cannot overflow
            if (total > MaxSeconds) return null;
        }

        return total > 0 ? total : null;
    }

    /// <summary>A plain count from 1 to <see cref="MaxUses"/>, or null.</summary>
    public static int? ParseUses(string text) =>
        text is { Length: <= 9 } && int.TryParse(text.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= MaxUses
            ? n
            : null;
}
