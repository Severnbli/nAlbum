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

using nAlbum.Bot.Ui;
using nAlbum.Data;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

public sealed class ViewScreen : Screen
{
    private const int ViewPage = 10;
    private const int ShowNumbersFlag = 1;
    private const int DeleteModeFlag = 2;
    private const int GridColumns = 5;
    private const int GridRows = 6;
    private const int GridPage = GridColumns * GridRows;
    private const int FlagMask = ShowNumbersFlag | DeleteModeFlag;

    public ViewScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "view", "vp", "rnd", "rp", "gp" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.Removing };
    public override IReadOnlyCollection<string> Commands => new[] { "/numbers" };

    public override Task OnText(MessageResult m, string text) => OnRemoveNumbers(m, text);

    /// <summary>
    /// /numbers: shows the file numbers under the album on screen; managers and the owner also get delete mode.
    /// The Done button of that page switches both off again.
    /// </summary>
    public override async Task OnCommand(MessageResult m, string command, IReadOnlyList<string> args)
    {
        await Ctx.Ui.TryDelete(m.MessageId); // keep the chat tidy

        var v = Ctx.Session.View;
        var key = v.LastPageKey?.Split(':');
        Album album = null;
        if (v.NavId != 0 && key is { Length: 3 }
            && long.TryParse(key[0], out var albumId) && long.TryParse(key[1], out var seed) && int.TryParse(key[2], out var offset))
        {
            album = await Ctx.Albums.GetAlbum(albumId);
            if (album != null && await Ctx.Albums.CanView(album, Ctx.UserId))
            {
                var flags = ShowNumbersFlag;
                if (await Ctx.Albums.CanManage(album, Ctx.UserId)) flags |= DeleteModeFlag;
                EnterViewMode(album, seed, offset, flags);
                await RenderPage(album, seed, offset, flags, v.NavId, null);
                return;
            }
        }

        await Ctx.Ui.Say(Ctx.T(LocKey.ViewNumbersNoView), Ctx.Ui.MenuButtons());
    }

    /// <summary>Media sent in delete mode: the matching items are deleted, like typed numbers.</summary>
    public override async Task OnMedia(DataResult data)
    {
        var picked = await Ctx.Ui.CollectPickedMedia(data);
        if (picked == null) return;
        var (items, missing) = picked.Value;

        var s = Ctx.Session;
        var album = await Ctx.Albums.GetAlbum(s.AlbumId);
        if (album == null || !await Ctx.Albums.CanManage(album, Ctx.UserId) || s.Mode != Mode.Removing)
        {
            if (s.Mode == Mode.Removing) s.Mode = Mode.Idle;
            return;
        }

        var note = items.Count == 0 ? Ctx.F(LocKey.MediaNotInAlbum, missing) : null;
        if (items.Count > 0)
        {
            var removed = await Ctx.Media.DeleteMediaInAlbum(album.Id, items.Select(i => i.Id));
            note = Ctx.F(LocKey.ViewDeleted, removed) + "."
                   + (missing > 0 ? "\n" + Ctx.F(LocKey.MediaNotInAlbum, missing) : "");
            await RenderPage(album, s.View.DeleteSeed, s.View.DeleteOffset, s.View.DeleteFlags, s.View.NavId, note);
            if (s.View.NavId != 0)
                s.View.LastPageKey = $"{album.Id}:{s.View.DeleteSeed}:{s.View.DeleteOffset}";
            return;
        }

        var total = await Ctx.Media.MediaCount(album.Id);
        var count = Math.Min(ViewPage, Math.Max(total - s.View.DeleteOffset, 0));
        var (navText, navButtons) = PageNav(album, s.View.DeleteSeed, s.View.DeleteOffset, total, count,
            s.View.DeleteFlags, note);
        await Ctx.Ui.EditNav(navText, navButtons);
    }

    public override Task OnCallback(MessageResult m, string[] p) =>
        p[0] == "gp" ? OnPageGrid(m, p) : OnPageView(m, p);

    /// <summary>
    /// gp:&lt;album&gt;:&lt;seed&gt;:&lt;offset&gt;:&lt;flags&gt;:&lt;grid page&gt; replaces the navigation text with a grid of page numbers.
    /// seed and offset belong to the page to come back to with Back.
    /// </summary>
    private async Task OnPageGrid(MessageResult m, string[] p)
    {
        var album = await Ctx.Ui.ViewableAlbum(m, p);
        if (album == null) return;
        await m.ConfirmAction();

        var seed = Math.Clamp(Text.Long(p, 2), 0, 2147483646);
        var offset = Math.Max(Text.Int(p, 3), 0);
        var flags = Text.Int(p, 4) & FlagMask;
        var total = await Ctx.Media.MediaCount(album.Id);
        var pages = (total + ViewPage - 1) / ViewPage;
        if (pages <= 1) return;

        var gridPages = (pages + GridPage - 1) / GridPage;
        var grid = Math.Clamp(Text.Int(p, 5), 0, gridPages - 1);
        var lastOffset = Math.Max(total - ViewPage, 0);
        var current = offset >= lastOffset ? pages : Math.Min(offset / ViewPage + 1, pages);

        var bf = new ButtonForm();
        var first = grid * GridPage + 1;
        var last = Math.Min(first + GridPage - 1, pages);
        var row = new List<ButtonBase>();
        for (var n = first; n <= last; n++)
        {
            row.Add(new ButtonBase(n == current ? $"•{n}•" : n.ToString(), PageCb(album, seed, (n - 1) * ViewPage, flags)));
            if (row.Count < GridColumns && n < last) continue;
            bf.AddButtonRow(row.ToArray());
            row.Clear();
        }

        if (gridPages > 1) // arrows only: the grid itself already shows where we are
        {
            string GridCb(int g) => $"gp:{album.Id}:{seed}:{offset}:{flags}:{g}";
            bf.AddButtonRow(
                new ButtonBase("◀", grid > 0 ? GridCb(grid - 1) : BotUi.FirstPageCallback),
                new ButtonBase("▶", grid < gridPages - 1 ? GridCb(grid + 1) : BotUi.LastPageCallback));
        }

        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), PageCb(album, seed, offset, flags));
        var title = Ctx.F(seed == 0 ? LocKey.ViewPageOrdered : LocKey.ViewPageRandom, Text.H(album.Title));
        await Ctx.Ui.EditNav(title + "\n\n" + Ctx.T(LocKey.ViewPickPage), bf);
    }

    /// <summary>Callback that re-opens a page: seed 0 = ordered view (vp), seed > 0 = random view (rp).</summary>
    private static string PageCb(Album album, long seed, int offset, int flags) =>
        seed == 0 ? $"vp:{album.Id}:{offset}:{flags}" : $"rp:{album.Id}:{seed}:{offset}:{flags}";

    /// <summary>
    /// view:&lt;album&gt;:&lt;offset&gt; | vp:&lt;album&gt;:&lt;offset&gt;:&lt;flags&gt;      ordered view
    /// rnd:&lt;album&gt;           | rp:&lt;album&gt;:&lt;seed&gt;:&lt;offset&gt;:&lt;flags&gt; random view
    /// flags: bit 1 = show numbers, bit 2 = delete mode.
    /// Missing flags = 0.
    /// </summary>
    private async Task OnPageView(MessageResult m, string[] p)
    {
        var album = await Ctx.Ui.ViewableAlbum(m, p);
        if (album == null) return;

        var random = p[0] is "rnd" or "rp";
        var isPage = p[0] is "vp" or "rp";
        var seed = random
            ? (isPage ? Math.Clamp(Text.Long(p, 2), 1, 2147483646) : Random.Shared.NextInt64(1, 2147483646))
            : 0L;
        var offset = isPage || p[0] == "view" ? Math.Max(Text.Int(p, random ? 3 : 2), 0) : 0;
        var flags = isPage ? Text.Int(p, random ? 4 : 3) : 0;
        flags &= FlagMask;
        if ((flags & DeleteModeFlag) != 0 && !await Ctx.Albums.CanManage(album, Ctx.UserId))
            flags &= ShowNumbersFlag; // only managers can delete
        if ((flags & DeleteModeFlag) != 0) flags |= ShowNumbersFlag; // delete mode always shows numbers

        var total = await Ctx.Media.MediaCount(album.Id);
        if (total == 0)
        {
            await m.ConfirmAction(Ctx.T(LocKey.ViewEmpty));
            return;
        }
        offset = Math.Min(offset, Math.Max(total - ViewPage, 0));

        await m.ConfirmAction();

        var key = $"{album.Id}:{seed}:{offset}";
        var isEntry = p[0] is "view" or "rnd";
        if (isEntry) await Ctx.Stats.RecordViewAsync(album.Id, Ctx.UserId, random);
        if (isEntry || key != Ctx.Session.View.LastPageKey)
            await Ctx.Stats.RecordPageAsync(album.Id, Ctx.UserId);

        EnterViewMode(album, seed, offset, flags);

        await RenderPage(album, seed, offset, flags, m.MessageId, null);
        Ctx.Session.View.LastPageKey = key;
    }

    /// <summary>Delete mode listens for typed numbers and media; any other view leaves it.</summary>
    private void EnterViewMode(Album album, long seed, int offset, int flags)
    {
        var s = Ctx.Session;
        if ((flags & DeleteModeFlag) != 0)
        {
            s.Mode = Mode.Removing;
            s.AlbumId = album.Id;
            s.View.DeleteSeed = seed;      // 0 = ordered view
            s.View.DeleteOffset = offset;
            s.View.DeleteFlags = flags;
        }
        else if (s.Mode == Mode.Removing)
        {
            s.Mode = Mode.Idle;            // left delete mode (Done / Reshuffle / Random / Back)
            s.View.DeleteSeed = 0;
        }
        // Mode.RemovePrompt is deliberately kept: the owner may browse and then type numbers for the preview.
    }

    private async Task RenderPage(Album album, long seed, int offset, int flags, int clickedMessageId, string note)
    {
        var total = await Ctx.Media.MediaCount(album.Id);
        if (total == 0)
        {
            await Ctx.View.ClearView();
            Ctx.Session.Mode = Mode.Idle;
            Ctx.Session.View.DeleteSeed = 0;
            var (card, cardButtons) = await Ctx.Cards.Build(album);
            await Ctx.Ui.Say(Ctx.T(LocKey.ViewNowEmpty) + card, cardButtons);
            return;
        }

        offset = Math.Min(offset, Math.Max(total - ViewPage, 0)); // shift the last page back to keep it full
        if ((flags & DeleteModeFlag) != 0) Ctx.Session.View.DeleteOffset = offset;

        var items = seed == 0
            ? await Ctx.Media.ListMedia(album.Id, offset, ViewPage)
            : await Ctx.Media.ListMediaShuffled(album.Id, seed, offset, ViewPage);

        List<string> captions = null;
        if ((flags & ShowNumbersFlag) != 0)
            captions = MediaView.NumberCaptions(items.Select(i => i.Number).ToList(), Ctx.T);

        var (text, nav) = PageNav(album, seed, offset, total, items.Count, flags, note);
        await Ctx.View.ShowViewPage(
            clickedMessageId,
            items,
            failed => text + (failed > 0 ? "\n\n" + Ctx.F(LocKey.ViewItemsFailed, failed) : ""),
            nav,
            captions);
    }

    private (string Text, ButtonForm Buttons) PageNav(Album album, long seed, int offset, int total, int count,
        int flags, string note)
    {
        var deleting = (flags & DeleteModeFlag) != 0;
        var numbers = (flags & ShowNumbersFlag) != 0;

        var text = Ctx.F(seed == 0 ? LocKey.ViewPageOrdered : LocKey.ViewPageRandom, Text.H(album.Title));
        var hints = new List<string>();
        if (numbers) hints.Add(Ctx.T(LocKey.ViewNumbersHint).TrimStart('\n'));
        if (deleting) hints.Add(Ctx.T(LocKey.ViewDeleteHint).TrimStart('\n'));
        if (hints.Count > 0) text += "\n\n" + string.Join("\n\n", hints);
        if (note != null) text = note + "\n\n" + text;

        var bf = new ButtonForm();
        var lastOffset = Math.Max(total - ViewPage, 0);
        if (lastOffset > 0) // more than one page: both arrows always shown, a popup at either end
        {
            var previousOffset = offset == lastOffset
                                 && lastOffset % ViewPage != 0
                ? lastOffset / ViewPage * ViewPage
                : Math.Max(offset - ViewPage, 0);
            var pages = (total + ViewPage - 1) / ViewPage;
            var current = offset >= lastOffset ? pages : Math.Min(offset / ViewPage + 1, pages);
            bf.AddButtonRow(
                new ButtonBase("◀", offset > 0 ? PageCb(album, seed, previousOffset, flags) : BotUi.FirstPageCallback),
                new ButtonBase($"{current}/{pages}", "noop"),
                new ButtonBase("▶",
                    offset < lastOffset ? PageCb(album, seed, Math.Min(offset + ViewPage, lastOffset), flags) : BotUi.LastPageCallback));
            bf.AddButtonRow(Ctx.T(LocKey.ButtonGoToPage), $"gp:{album.Id}:{seed}:{offset}:{flags}:{(current - 1) / GridPage}");
        }

        if (numbers) bf.AddButtonRow(Ctx.T(LocKey.ButtonDone), PageCb(album, seed, offset, 0));

        bf.AddButtonRow(
            new ButtonBase(Ctx.T(seed == 0 ? LocKey.ButtonRandom : LocKey.ButtonReshuffle), $"rnd:{album.Id}"),
            new ButtonBase(Ctx.T(LocKey.ButtonBack), $"al:{album.Id}"));
        return (text, bf);
    }

    private async Task OnRemoveNumbers(MessageResult message, string text)
    {
        var s = Ctx.Session;
        var album = await Ctx.Albums.GetAlbum(s.AlbumId);
        if (album == null || !await Ctx.Albums.CanManage(album, Ctx.UserId))
        {
            s.Mode = Mode.Idle;
            return;
        }

        await Ctx.Ui.TryDelete(message.MessageId); // keep the chat tidy: remove the typed message

        var numbers = NumberParser.Parse(text);
        var total = await Ctx.Media.MediaCount(album.Id);
        string note;
        var deleted = false;

        if (numbers == null || numbers.Count == 0)
        {
            note = Ctx.T(LocKey.ViewUnreadable);
        }
        else
        {
            var found = await Ctx.Media.ResolveNumbers(album.Id, numbers);
            if (found.Count == 0)
            {
                note = Ctx.T(LocKey.ViewNoMatches);
            }
            else
            {
                var removed = await Ctx.Media.DeleteMediaInAlbum(album.Id, found.Select(x => x.Item.Id));
                note = Ctx.F(LocKey.ViewDeleted, removed) +
                       (found.Count < numbers.Count ? Ctx.F(LocKey.ViewDeletedNotFound, numbers.Count - found.Count) : ".");
                deleted = true;
            }
        }

        if (deleted)
        {
            await RenderPage(album, s.View.DeleteSeed, s.View.DeleteOffset, s.View.DeleteFlags, s.View.NavId, note); // refresh in place
            if (s.View.NavId != 0)
                s.View.LastPageKey = $"{album.Id}:{s.View.DeleteSeed}:{s.View.DeleteOffset}";
            return;
        }

        var count = Math.Min(ViewPage, Math.Max(total - s.View.DeleteOffset, 0));
        var (navText, navButtons) = PageNav(album, s.View.DeleteSeed, s.View.DeleteOffset, total, count, s.View.DeleteFlags, note);
        await Ctx.Ui.EditNav(navText, navButtons);
    }
}
