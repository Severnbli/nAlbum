using nAlbum.Bot.Ui;
using nAlbum.Data;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

public sealed class RemoveScreen : Screen
{
    private const int ViewPage = 10;

    public RemoveScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "rmlist", "rmt", "rmp", "rmy", "rmc" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.RemovePrompt };

    public override Task OnText(MessageResult m, string text) => OnRemovePromptText(m, text);

    /// <summary>Media sent in the prompt: the matching items join the selection and the preview is shown.</summary>
    public override async Task OnMedia(DataResult data)
    {
        var picked = await Ctx.Ui.CollectPickedMedia(data);
        if (picked == null) return;
        var (items, missing) = picked.Value;

        var album = await Ctx.Albums.GetAlbum(Ctx.Session.AlbumId);
        if (album == null || album.OwnerId != Ctx.UserId || Ctx.Session.Mode != Mode.RemovePrompt)
        {
            if (Ctx.Session.Mode == Mode.RemovePrompt) Ctx.Session.Mode = Mode.Idle;
            return;
        }

        var note = missing > 0 ? Ctx.F(LocKey.MediaNotInAlbum, missing) : null;
        if (items.Count == 0)
        {
            await RefreshRemoveScreen(album, note);
            return;
        }

        var r = Ctx.Session.Removal;
        if (r.AlbumId != album.Id)
        {
            r.Pending.Clear();
            r.Kept.Clear();
        }

        foreach (var item in items)
        {
            if (r.Pending.All(x => x.Item.Id != item.Id)) r.Pending.Add((item.Number, item));
            r.Kept.Remove(item.Id);
        }

        r.Pending.Sort((a, b) => a.Number.CompareTo(b.Number));
        r.AlbumId = album.Id;
        await RenderRemovePreview(album, 0, Ctx.Session.View.NavId != 0 ? Ctx.Session.View.NavId : r.PromptMsgId, note);
    }

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        if (p[0] == "rmlist") await OnRemovePrompt(m, p);
        else await OnRemovePreviewAction(m, p);
    }

    /// <summary>Card button LocKey.ButtonRemoveMedia: turns the card message into the "type numbers" prompt.</summary>
    private async Task OnRemovePrompt(MessageResult m, string[] p)
    {
        var album = await Ctx.Ui.OwnedAlbum(m, p);
        if (album == null) return;

        if (await Ctx.Media.MediaCount(album.Id) == 0)
        {
            await m.ConfirmAction(Ctx.T(LocKey.RemoveNothing), true);
            return;
        }

        await m.ConfirmAction();
        await Ctx.View.ClearView(m.MessageId);   // drop any album still on screen, keep the clicked message
        Ctx.Session.Removal.Clear();
        Ctx.Session.Mode = Mode.RemovePrompt;
        Ctx.Session.AlbumId = album.Id;
        Ctx.Session.Removal.PromptMsgId = m.MessageId;
        await RenderRemovePrompt(album, null);
    }

    private async Task RenderRemovePrompt(Album album, string note)
    {
        var r = Ctx.Session.Removal;
        var total = await Ctx.Media.MediaCount(album.Id);
        var text = Ctx.F(LocKey.RemoveTitle, Text.H(album.Title), total) + "\n\n" +
                   Ctx.T(LocKey.RemovePrompt);
        if (note != null) text = note + "\n\n" + text;

        var bf = new ButtonForm();
        bf.AddButtonRow(Ctx.T(LocKey.ButtonViewWithNumbers), $"vp:{album.Id}:0:1");
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBackCancel), $"al:{album.Id}");

        if (r.PromptMsgId != 0)
        {
            try
            {
                await Ctx.Device.Edit(r.PromptMsgId, text, bf, ParseMode.Html);
                return;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
            {
                return;
            }
            catch (ApiRequestException)
            {
                // prompt message is gone -> send a new one
            }
        }

        r.PromptMsgId = (await Ctx.Ui.Say(text, bf))?.MessageId ?? 0;
    }

    /// <summary>Typed numbers in Mode.RemovePrompt: build (or replace) the selection and show the preview. Deletes nothing.</summary>
    private async Task OnRemovePromptText(MessageResult message, string text)
    {
        var album = await Ctx.Albums.GetAlbum(Ctx.Session.AlbumId);
        if (album == null || album.OwnerId != Ctx.UserId)
        {
            Ctx.Session.Mode = Mode.Idle;
            return;
        }

        await Ctx.Ui.TryDelete(message.MessageId); // keep the chat tidy

        var numbers = NumberParser.Parse(text);
        if (numbers == null || numbers.Count == 0)
        {
            await RefreshRemoveScreen(album, Ctx.T(LocKey.RemoveUnreadable));
            return;
        }

        var resolved = await Ctx.Media.ResolveNumbers(album.Id, numbers);
        if (resolved.Count == 0)
        {
            await RefreshRemoveScreen(album, Ctx.T(LocKey.RemoveNoMatches));
            return;
        }

        var r = Ctx.Session.Removal;
        r.Pending.Clear();
        r.Kept.Clear();
        r.Pending.AddRange(resolved);
        r.AlbumId = album.Id;

        var skipped = numbers.Count - resolved.Count;
        var note = skipped > 0 ? Ctx.F(LocKey.RemoveIgnored, skipped) : null;
        await RenderRemovePreview(album, 0, Ctx.Session.View.NavId != 0 ? Ctx.Session.View.NavId : r.PromptMsgId, note);
    }

    /// <summary>Shows a one-line note without redrawing media: on the preview nav if one is on screen, else on the prompt.</summary>
    private async Task RefreshRemoveScreen(Album album, string note)
    {
        var r = Ctx.Session.Removal;
        if (r.Pending.Count > 0 && Ctx.Session.View.NavId != 0)
        {
            var page = r.Pending.Skip(r.PreviewOffset).Take(ViewPage).ToList();
            var (text, bf) = RemovePreviewNav(album, r.PreviewOffset, page, note);
            await Ctx.Ui.EditNav(text, bf);
            return;
        }

        await RenderRemovePrompt(album, note);
    }

    private async Task RenderRemovePreview(Album album, int offset, int clickedMessageId, string note)
    {
        var r = Ctx.Session.Removal;
        var lastPageStart = Math.Max(r.Pending.Count - 1, 0) / ViewPage * ViewPage;
        offset = Math.Clamp(offset, 0, lastPageStart);
        r.PreviewOffset = offset;

        var page = r.Pending.Skip(offset).Take(ViewPage).ToList();
        var items = page.Select(x => x.Item).ToList();
        var captions = MediaView.NumberCaptions(page.Select(x => x.Number).ToList(), Ctx.T);
        var (text, nav) = RemovePreviewNav(album, offset, page, note);

        await Ctx.View.ShowViewPage(
            clickedMessageId,
            items,
            failed => text + (failed > 0 ? "\n" + Ctx.F(LocKey.RemovePreviewFailed, failed) : ""),
            nav,
            captions);
        r.PromptMsgId = 0; // the prompt message was replaced by the preview
    }

    private (string Text, ButtonForm Buttons) RemovePreviewNav(Album album, int offset,
        List<(int Number, MediaItem Item)> page, string note)
    {
        var r = Ctx.Session.Removal;
        var toDelete = r.Pending.Count(x => !r.Kept.Contains(x.Item.Id));

        var text = Ctx.F(LocKey.RemovePreview,
            Text.H(album.Title), r.Pending.Count, offset + 1, offset + page.Count, toDelete);
        if (note != null) text = note + "\n\n" + text;

        var bf = new ButtonForm();
        var row = new List<ButtonBase>();
        foreach (var (number, item) in page)
        {
            var kept = r.Kept.Contains(item.Id);
            row.Add(new ButtonBase($"{(kept ? "↩" : "🗑")} #{number}", $"rmt:{item.Id}:{offset}"));
            if (row.Count == 5)
            {
                bf.AddButtonRow(row.ToArray());
                row = new List<ButtonBase>();
            }
        }

        if (row.Count > 0) bf.AddButtonRow(row.ToArray());

        var navRow = new List<ButtonBase>();
        if (offset > 0) navRow.Add(new ButtonBase(Ctx.T(LocKey.ButtonPrev), $"rmp:{album.Id}:{Math.Max(offset - ViewPage, 0)}"));
        if (offset + ViewPage < r.Pending.Count) navRow.Add(new ButtonBase(Ctx.T(LocKey.ButtonNext), $"rmp:{album.Id}:{offset + ViewPage}"));
        if (navRow.Count > 0) bf.AddButtonRow(navRow.ToArray());

        var actions = new List<ButtonBase>();
        if (toDelete > 0) actions.Add(new ButtonBase(Ctx.F(LocKey.ButtonDeleteCount, toDelete), $"rmy:{album.Id}"));
        actions.Add(new ButtonBase(Ctx.T(LocKey.ButtonCancelAction), $"rmc:{album.Id}"));
        bf.AddButtonRow(actions.ToArray());

        return (text, bf);
    }

    /// <summary>The preview is live only for the owner, in Mode.RemovePrompt, with a non-empty selection of that album.</summary>
    private async Task<Album> LivePreview(MessageResult m, long albumId)
    {
        var r = Ctx.Session.Removal;
        if (Ctx.Session.Mode != Mode.RemovePrompt || r.Pending.Count == 0 || m.MessageId != Ctx.Session.View.NavId ||
            (albumId != 0 && r.AlbumId != albumId))
        {
            await m.ConfirmAction(Ctx.T(LocKey.RemovePreviewExpired), true);
            return null;
        }

        var album = await Ctx.Albums.GetAlbum(r.AlbumId);
        if (album == null || album.OwnerId != Ctx.UserId)
        {
            await m.ConfirmAction(Ctx.T(LocKey.ErrorAlbumNotOwned), true);
            return null;
        }

        return album;
    }

    private async Task OnRemovePreviewAction(MessageResult m, string[] p)
    {
        var r = Ctx.Session.Removal;
        var album = await LivePreview(m, p[0] == "rmt" ? 0 : Text.Long(p, 1));
        if (album == null) return;

        switch (p[0])
        {
            case "rmt": // toggle keep / delete: only the navigation message changes (no media edits)
            {
                var id = Text.Long(p, 1);
                if (r.Pending.All(x => x.Item.Id != id))
                {
                    await m.ConfirmAction(Ctx.T(LocKey.RemovePreviewExpired), true);
                    return;
                }

                await m.ConfirmAction();
                if (!r.Kept.Remove(id)) r.Kept.Add(id);
                r.PreviewOffset = Math.Max(Text.Int(p, 2), 0);
                await RefreshRemoveScreen(album, null);
                break;
            }

            case "rmp": // preview page
                await m.ConfirmAction();
                await RenderRemovePreview(album, Text.Int(p, 2), m.MessageId, null);
                break;

            case "rmc": // cancel everything
            {
                await m.ConfirmAction(Ctx.T(LocKey.RemoveCancelled));
                r.Clear();
                Ctx.Session.Mode = Mode.Idle;
                await Ctx.View.ClearView(m.MessageId);
                var (card, bf) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Show(m, card, bf);
                break;
            }

            case "rmy": // confirm: delete everything that was not kept
            {
                var ids = r.Pending.Where(x => !r.Kept.Contains(x.Item.Id)).Select(x => x.Item.Id).ToList();
                if (ids.Count == 0)
                {
                    await m.ConfirmAction(Ctx.T(LocKey.RemoveNothingSelected), true);
                    return;
                }

                await m.ConfirmAction();
                var deleted = await Ctx.Media.DeleteMediaInAlbum(album.Id, ids);
                r.Clear();
                Ctx.Session.Mode = Mode.Idle;
                await Ctx.View.ClearView(m.MessageId);
                var (card, bf) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Show(m, Ctx.F(LocKey.RemoveDeleted, deleted) + card, bf);
                break;
            }
        }
    }
}
