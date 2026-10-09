using nAlbum.Data;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using TelegramBotBase.Sessions;
using nAlbum.Localization;

namespace nAlbum.Bot.Ui;

public sealed class BotUi
{
    private const int ListPage = 8;

    public BotUi(BotContext ctx) { Ctx = ctx; }
    private BotContext Ctx { get; }

    /// <summary>Sends a new HTML message.</summary>
    public Task<Message> Say(string html, ButtonForm bf = null) =>
        Ctx.Device.Dispatch(a => a.SendMessage(Ctx.Device.DeviceId, html, parseMode: ParseMode.Html,
            replyMarkup: Markup(bf), linkPreviewOptions: NoLinkPreview));

    private static readonly LinkPreviewOptions NoLinkPreview = new() { IsDisabled = true };

    private static InlineKeyboardMarkup Markup(ButtonForm bf) =>
        bf == null ? null : new InlineKeyboardMarkup(bf.ToInlineButtonArray());

    private Task EditText(int messageId, string html, ButtonForm bf) =>
        Ctx.Device.Dispatch(a => a.EditMessageText(Ctx.Device.DeviceId, messageId, html, parseMode: ParseMode.Html,
            replyMarkup: Markup(bf), linkPreviewOptions: NoLinkPreview));

    /// <summary>Edits the message the button belongs to; falls back to a new message.</summary>
    public async Task Show(MessageResult m, string html, ButtonForm bf)
    {
        if (m.IsAction)
        {
            try
            {
                await EditText(m.MessageId, html, bf);
                return;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
            {
                return;
            }
            catch (ApiRequestException)
            {
                // message too old / not editable -> send a fresh one
            }
        }

        await Say(html, bf);
    }

    public async Task TryDelete(int messageId)
    {
        try
        {
            await Ctx.Device.DeleteMessage(messageId);
        }
        catch (ApiRequestException)
        {
        }
    }

    /// <summary>Edits the navigation message of the current view (or sends one if there is none).</summary>
    public async Task EditNav(string html, ButtonForm bf)
    {
        var v = Ctx.Session.View;
        if (v.NavId == 0)
        {
            v.NavId = (await Say(html, bf))?.MessageId ?? 0;
            return;
        }

        try
        {
            await EditText(v.NavId, html, bf);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
        {
        }
        catch (ApiRequestException ex)
        {
            await Console.Error.WriteLineAsync($"Could not edit navigation message: {ex.Message}");
        }
    }

    public ButtonForm MenuButtons()
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(Ctx.T(LocKey.MenuNewAlbum), "new");
        bf.AddButtonRow(Ctx.T(LocKey.MenuMyAlbums), "mine:0");
        bf.AddButtonRow(Ctx.T(LocKey.MenuSharedWithMe), "shared:0");
        bf.AddButtonRow(Ctx.T(LocKey.MenuGlobalStatistics), "gstats");
        if (Ctx.Config.IsAdmin(Ctx.UserId)) bf.AddButtonRow(Ctx.T(LocKey.MenuDetailedStatistics), "stats");
        bf.AddButtonRow(Ctx.T(LocKey.MenuLanguage), "language");
        return bf;
    }

    public async Task ShowList(MessageResult m, string kind, int page)
    {
        page = Math.Max(page, 0);
        var (items, total) = kind == "mine"
            ? await Ctx.Albums.ListOwned(Ctx.UserId, page * ListPage, ListPage)
            : await Ctx.Albums.ListShared(Ctx.UserId, page * ListPage, ListPage);

        if (items.Count == 0 && total > 0) // page is out of range after deletions
        {
            page = (total - 1) / ListPage;
            (items, total) = kind == "mine"
                ? await Ctx.Albums.ListOwned(Ctx.UserId, page * ListPage, ListPage)
                : await Ctx.Albums.ListShared(Ctx.UserId, page * ListPage, ListPage);
        }

        var text = kind == "mine" ? Ctx.T(LocKey.ListMineTitle) : Ctx.T(LocKey.ListSharedTitle);
        if (total == 0)
        {
            text += Ctx.T(LocKey.ListEmpty) + (kind == "shared" ? Ctx.T(LocKey.ListSharedEmptyHint) : "");
        }

        var bf = new ButtonForm();
        foreach (var it in items)
        {
            var icon = kind == "mine" ? (it.Album.IsOpen ? "🔓" : "🔒") : "📁";
            bf.AddButtonRow($"{icon} {Text.Truncate(it.Album.Title, 40)} ({it.MediaCount})", $"al:{it.Album.Id}");
        }

        var nav = new List<ButtonBase>();
        if (page > 0) nav.Add(new ButtonBase("◀", $"{kind}:{page - 1}"));
        if ((page + 1) * ListPage < total) nav.Add(new ButtonBase("▶", $"{kind}:{page + 1}"));
        if (nav.Count > 0) bf.AddButtonRow(nav.ToArray());
        if (kind == "mine") bf.AddButtonRow(Ctx.T(LocKey.MenuNewAlbum), "new");
        bf.AddButtonRow(Ctx.T(LocKey.ButtonMenu), "menu");

        await Show(m, text, bf);
    }

    public async Task<Album> OwnedAlbum(MessageResult m, string[] p)
    {
        var album = await Ctx.Albums.GetAlbum(Text.Long(p, 1));
        if (album != null && album.OwnerId == Ctx.UserId) return album;
        await m.ConfirmAction(Ctx.T(LocKey.ErrorAlbumNotOwned), true);
        return null;
    }

    public async Task<Album> ViewableAlbum(MessageResult m, string[] p)
    {
        var album = await Ctx.Albums.GetAlbum(Text.Long(p, 1));
        if (album != null && await Ctx.Albums.CanView(album, Ctx.UserId)) return album;
        await m.ConfirmAction(Ctx.T(LocKey.ErrorAlbumUnavailable), true);
        return null;
    }
}
