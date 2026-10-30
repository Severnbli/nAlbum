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
using TelegramBotBase.Form;

namespace nAlbum.Bot.Ui;

/// <summary>One page of a <see cref="Pager"/> result. Index 0 is the oldest page.</summary>
public sealed record PagedText(string Text, int Index, int Pages)
{
    /// <summary>◀ n/N ▶ row, or null for one page. The callback gets how many pages back from the newest one to open (0 = newest).</summary>
    public ButtonBase[] Nav(Func<int, string> callback) =>
        BotUi.PageRow(Index, Pages, 1, idx => callback(Pages - 1 - idx));
}

/// <summary>
/// Cuts a long list of text blocks into pages that stay short enough to read on a phone without scrolling.
/// Use it for any long record list (conversation history, logs, ...): turn each entry into blocks with
/// <see cref="Chunk"/>, pass them to <see cref="Build"/> and add <see cref="PagedText.Nav"/> to the buttons.
/// Open the newest page with <c>fromEnd = 0</c>; keep the page number in the callback data.
/// </summary>
public static class Pager
{
    public const int MaxRows = 28;     // text rows per page (twice a phone screen, a little scrolling is fine)
    public const int RowChars = 36;    // characters per row on a narrow phone
    public const int ChunkChars = 300; // longest piece of one entry; longer text continues in the next block

    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>Estimated screen rows of an HTML text.</summary>
    public static int Rows(string html)
    {
        var plain = Tags.Replace(html, "");
        return plain.Split('\n').Sum(line => Math.Max(1, (line.Length + RowChars - 1) / RowChars));
    }

    /// <summary>Cuts raw (not yet escaped) text into pieces of at most <paramref name="size"/> characters, at a space when possible.</summary>
    public static IEnumerable<string> Chunk(string raw, int size = ChunkChars)
    {
        raw ??= "";
        while (raw.Length > size)
        {
            var cut = raw.LastIndexOfAny(new[] { ' ', '\n' }, size - 1, size / 2);
            if (cut <= 0) cut = size;
            yield return raw[..cut].TrimEnd();
            raw = raw[cut..].TrimStart();
        }

        yield return raw;
    }

    /// <summary>
    /// Packs blocks (ready HTML, each shorter than a page) into pages; the header is repeated on every page.
    /// fromEnd = pages back from the newest one, clamped to the existing pages.
    /// maxRows raises the page size for static reading text (the default fits a phone without scrolling).
    /// </summary>
    public static PagedText Build(IReadOnlyList<string> blocks, string header, int fromEnd, int maxRows = MaxRows)
    {
        var pages = new List<string>();
        var current = new List<string>();
        var rows = Rows(header) + 1;
        foreach (var block in blocks)
        {
            var need = Rows(block) + 1;   // + the blank line between blocks
            if (current.Count > 0 && rows + need > maxRows)
            {
                pages.Add(string.Join("\n\n", current));
                current.Clear();
                rows = Rows(header) + 1;
            }

            current.Add(block);
            rows += need;
        }

        if (current.Count > 0 || pages.Count == 0) pages.Add(string.Join("\n\n", current));
        var index = Math.Clamp(pages.Count - 1 - Math.Max(fromEnd, 0), 0, pages.Count - 1);
        return new PagedText(header + "\n\n" + pages[index], index, pages.Count);
    }
}
