using nAlbum.Bot.Ui;
using nAlbum.Data;
using TelegramBotBase.Base;
using TelegramBotBase.Form;

namespace nAlbum.Bot.Screens;

public sealed class ViewScreen : Screen
{
    private const int ViewPage = 10;

    public ViewScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "view", "vp", "rnd", "rp" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.Removing };

    public override Task OnText(MessageResult m, string text) => OnRemoveNumbers(m, text);

    public override Task OnCallback(MessageResult m, string[] p) => OnPageView(m, p);

    /// <summary>Callback that re-opens a page: seed 0 = ordered view (vp), seed > 0 = random view (rp).</summary>
    private static string PageCb(Album album, long seed, int offset, int flags) =>
        seed == 0 ? $"vp:{album.Id}:{offset}:{flags}" : $"rp:{album.Id}:{seed}:{offset}:{flags}";

    /// <summary>
    /// view:&lt;album&gt;:&lt;offset&gt; | vp:&lt;album&gt;:&lt;offset&gt;:&lt;flags&gt;      ordered view
    /// rnd:&lt;album&gt;           | rp:&lt;album&gt;:&lt;seed&gt;:&lt;offset&gt;:&lt;flags&gt; random view
    /// flags: bit 1 = show numbers, bit 2 = delete mode (delete mode implies numbers). Missing flags = 0.
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
        if ((flags & 2) != 0 && album.OwnerId != Ctx.UserId) flags &= 1; // only the owner can delete
        if ((flags & 2) != 0) flags |= 1;                            // delete mode always shows numbers

        var total = await Ctx.Media.MediaCount(album.Id);
        if (total == 0)
        {
            await m.ConfirmAction(Ctx.T("This album is empty."), true);
            return;
        }
        if (offset >= total) offset = (total - 1) / ViewPage * ViewPage;

        await m.ConfirmAction();

        var key = $"{album.Id}:{seed}:{offset}";
        var isEntry = p[0] is "view" or "rnd";
        if (isEntry) await Ctx.Stats.RecordViewAsync(album.Id, Ctx.UserId, random);
        if (isEntry || key != Ctx.Session.View.LastPageKey)
            await Ctx.Stats.RecordPageAsync(album.Id, Ctx.UserId);

        if ((flags & 2) != 0)
        {
            Ctx.Session.Mode = Mode.Removing;
            Ctx.Session.AlbumId = album.Id;
            Ctx.Session.View.DeleteSeed = seed;      // 0 = ordered view
            Ctx.Session.View.DeleteOffset = offset;
        }
        else if (Ctx.Session.Mode == Mode.Removing)
        {
            Ctx.Session.Mode = Mode.Idle;       // left delete mode (Done deleting / Reshuffle / Random / Back to a plain page)
            Ctx.Session.View.DeleteSeed = 0;
        }
        // Mode.RemovePrompt is deliberately kept: the owner may browse with numbers and then type numbers for the preview.

        await RenderPage(album, seed, offset, flags, m.MessageId, null);
        Ctx.Session.View.LastPageKey = key;
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
            await Ctx.Ui.Say(Ctx.T("✅ The album is now empty.\n\n") + card, cardButtons);
            return;
        }

        if (offset >= total) offset = (total - 1) / ViewPage * ViewPage; // the page vanished after deletions
        if ((flags & 2) != 0) Ctx.Session.View.DeleteOffset = offset;

        var items = seed == 0
            ? await Ctx.Media.ListMedia(album.Id, offset, ViewPage)
            : await Ctx.Media.ListMediaShuffled(album.Id, seed, offset, ViewPage);

        List<string> captions = null;
        if ((flags & 1) != 0)
            captions = MediaView.NumberCaptions(items.Select(i => i.Number).ToList(), Ctx.T);

        var (text, nav) = PageNav(album, seed, offset, total, items.Count, flags, note);
        await Ctx.View.ShowViewPage(
            clickedMessageId,
            items,
            failed => text + (failed > 0 ? "\n" + Ctx.F("⚠️ {0} item(s) could not be sent.", failed) : ""),
            nav,
            captions);
    }

    private (string Text, ButtonForm Buttons) PageNav(Album album, long seed, int offset, int total, int count,
        int flags, string note)
    {
        var deleting = (flags & 2) != 0;
        var numbers = (flags & 1) != 0;

        var text = seed == 0
            ? Ctx.F("<b>{0}</b>: items {1}–{2} of {3}", Text.H(album.Title), offset + 1, offset + count, total)
            : Ctx.F("🎲 <b>{0}</b>: random {1}–{2} of {3}", Text.H(album.Title), offset + 1, offset + count, total);
        if (numbers) text += Ctx.T("\nThe numbers under the pictures are listed in the same order as the pictures (left to right, top to bottom). Numbers never change.");
        if (deleting) text += Ctx.T("\n🗑 Type the number(s) to delete, e.g. <code>12</code>, <code>12 15 18</code> or <code>12-15</code>.");
        if (note != null) text = note + "\n\n" + text;

        var bf = new ButtonForm();
        var row = new List<ButtonBase>();
        if (offset > 0)
            row.Add(new ButtonBase(Ctx.T("◀ Prev"), PageCb(album, seed, Math.Max(offset - ViewPage, 0), flags)));
        if (offset + ViewPage < total)
            row.Add(new ButtonBase(Ctx.T("Next ▶"), PageCb(album, seed, offset + ViewPage, flags)));
        if (row.Count > 0) bf.AddButtonRow(row.ToArray());

        if (deleting)
        {
            bf.AddButtonRow(Ctx.T("✅ Done deleting"), PageCb(album, seed, offset, 1));
        }
        else
        {
            var toggle = numbers
                ? new ButtonBase(Ctx.T("🔢 Hide numbers"), PageCb(album, seed, offset, flags & ~1))
                : new ButtonBase(Ctx.T("🔢 Show numbers"), PageCb(album, seed, offset, flags | 1));
            var buttons = new List<ButtonBase> { toggle };
            if (album.OwnerId == Ctx.UserId)
                buttons.Add(new ButtonBase(Ctx.T("🗑 Delete by number"), PageCb(album, seed, offset, 3)));
            bf.AddButtonRow(buttons.ToArray());
        }

        bf.AddButtonRow(
            new ButtonBase(Ctx.T(seed == 0 ? "🎲 Random" : "🎲 Reshuffle"), $"rnd:{album.Id}"),
            new ButtonBase(Ctx.T("⬅ Back"), $"al:{album.Id}"));
        return (text, bf);
    }

    private async Task OnRemoveNumbers(MessageResult message, string text)
    {
        var s = Ctx.Session;
        var album = await Ctx.Albums.GetAlbum(s.AlbumId);
        if (album == null || album.OwnerId != Ctx.UserId)
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
            note = Ctx.T("⚠️ I couldn't read that.");
        }
        else
        {
            var found = await Ctx.Media.ResolveNumbers(album.Id, numbers);
            if (found.Count == 0)
            {
                note = Ctx.T("⚠️ No items with those numbers (already deleted or never existed).");
            }
            else
            {
                var removed = await Ctx.Media.DeleteMediaInAlbum(album.Id, found.Select(x => x.Item.Id));
                note = Ctx.F("✅ Deleted {0} item(s)", removed) +
                       (found.Count < numbers.Count ? Ctx.F(" ({0} number(s) not found).", numbers.Count - found.Count) : ".");
                deleted = true;
            }
        }

        if (deleted)
        {
            await RenderPage(album, s.View.DeleteSeed, s.View.DeleteOffset, 3, s.View.NavId, note); // refresh in place
            if (s.View.NavId != 0)
                s.View.LastPageKey = $"{album.Id}:{s.View.DeleteSeed}:{s.View.DeleteOffset}";
            return;
        }

        var count = Math.Min(ViewPage, Math.Max(total - s.View.DeleteOffset, 0));
        var (navText, navButtons) = PageNav(album, s.View.DeleteSeed, s.View.DeleteOffset, total, count, 3, note);
        await Ctx.Ui.EditNav(navText, navButtons);
    }
}
