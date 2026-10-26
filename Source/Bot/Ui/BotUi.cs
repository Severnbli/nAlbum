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

using nAlbum.Bot.Screens;
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

    /// <summary>
    /// Sends a new HTML message. A message with buttons becomes the menu message and replaces the previous one,
    /// so the chat never holds two live menus.
    /// </summary>
    public async Task<Message> Say(string html, ButtonForm bf = null)
    {
        var msg = await SayNav(html, bf);
        if (bf != null && msg != null) await ReplaceMenu(msg.MessageId);
        return msg;
    }

    /// <summary>Sends a message that is not tracked as the menu (view navigation messages).</summary>
    public Task<Message> SayNav(string html, ButtonForm bf = null) =>
        Ctx.Device.Dispatch(a => a.SendMessage(Ctx.Device.DeviceId, html, parseMode: ParseMode.Html,
            replyMarkup: Markup(bf), linkPreviewOptions: NoLinkPreview));

    /// <summary>Makes messageId the menu message and deletes the previous one (unless it is the view navigation).</summary>
    private async Task ReplaceMenu(int messageId)
    {
        var s = Ctx.Session;
        var previous = s.MenuMessageId;
        s.MenuMessageId = messageId;
        if (previous != 0 && previous != messageId && previous != s.View.NavId) await TryDelete(previous);
    }

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
                await ReplaceMenu(m.MessageId);
                return;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
            {
                await ReplaceMenu(m.MessageId);
                return;
            }
            catch (ApiRequestException)
            {
                // message too old / not editable -> send a fresh one
            }
        }

        await Say(html, bf);
    }

    /// <summary>Edits the current menu message (e.g. a prompt after invalid input); falls back to a new message.</summary>
    public async Task ShowMenu(string html, ButtonForm bf)
    {
        var id = Ctx.Session.MenuMessageId;
        if (id != 0)
        {
            try
            {
                await EditText(id, html, bf);
                return;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
            {
                return;
            }
            catch (ApiRequestException)
            {
                // menu message is gone -> send a fresh one
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
            v.NavId = (await SayNav(html, bf))?.MessageId ?? 0;
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

    public async Task<(List<MediaItem> Items, int Missing)?> CollectPickedMedia(DataResult data)
    {
        var msg = data.Message;
        if (!AddMediaScreen.TryExtract(msg, out var media)) return null;

        await TryDelete(msg.MessageId); // keep the chat tidy
        var item = await Ctx.Media.FindByUniqueId(Ctx.Session.AlbumId, media.UniqueId);
        var pick = Ctx.Session.Removal.Pick;
        var ticket = await pick.Add(item);
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        return await pick.Take(ticket);
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
