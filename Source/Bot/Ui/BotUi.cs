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
        Ctx.Device.Dispatch(a => a.SendMessage(Ctx.Device.DeviceId, Text.WithMinWidth(html, bf), parseMode: ParseMode.Html,
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
        Ctx.Device.Dispatch(a => a.EditMessageText(Ctx.Device.DeviceId, messageId, Text.WithMinWidth(html, bf), parseMode: ParseMode.Html,
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

    /// <summary>
    /// Sends a plain notice to another user in their language, ignoring failures (they may have blocked the bot).
    /// Pass the language resolved beforehand when their data is about to be deleted.
    /// </summary>
    public async Task Notify(long chatId, string key, string language = null)
    {
        try
        {
            language ??= Ctx.Localization.Resolve(await Ctx.Preferences.GetLanguageAsync(chatId), null);
            await Ctx.Device.Dispatch(a => a.SendMessage(chatId, Ctx.Localization.T(language, key), parseMode: ParseMode.Html));
        }
        catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Notice to {chatId} failed: {ex.Message}");
        }
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

    /// <summary>Callback of a ◀ / ▶ button at the start or end of a list: shows a popup instead of turning the page.</summary>
    public const string FirstPageCallback = "pgend:first";
    public const string LastPageCallback = "pgend:last";

    /// <summary>
    /// Navigation row of a paged list: ◀ page/pages ▶. Both arrows are always there so the buttons never move;
    /// at either end they show a popup. The counter button does nothing. Null when everything fits on one page.
    /// </summary>
    public static ButtonBase[] PageRow(int page, int total, int pageSize, Func<int, string> callback)
    {
        var pages = Math.Max((total + pageSize - 1) / pageSize, 1);
        if (pages <= 1) return null;
        page = Math.Clamp(page, 0, pages - 1);

        return new[]
        {
            new ButtonBase("◀", page > 0 ? callback(page - 1) : FirstPageCallback),
            new ButtonBase($"{page + 1}/{pages}", "noop"),
            new ButtonBase("▶", page < pages - 1 ? callback(page + 1) : LastPageCallback)
        };
    }

    public ButtonForm MenuButtons()
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.MenuAlbums), "albums:0"), new ButtonBase(Ctx.T(LocKey.MenuNewAlbum), "new"));
        bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.MenuGlobalStatistics), "gstats"), new ButtonBase(Ctx.T(LocKey.MenuLanguage), "language"));
        bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.MenuHelp), "help"), new ButtonBase(Ctx.T(LocKey.MenuPrivacy), "privacy"));
        if (Ctx.Config.IsAdmin(Ctx.UserId)) bf.AddButtonRow(Ctx.T(LocKey.MenuAdminPanel), "admin");
        return bf;
    }

    /// <summary>Album overview: every album the user owns or has access to.</summary>
    public async Task ShowList(MessageResult m, int page)
    {
        page = Math.Max(page, 0);
        var (items, total) = await Ctx.Albums.ListAccessible(Ctx.UserId, page * ListPage, ListPage);
        if (items.Count == 0 && total > 0) // page is out of range after deletions
        {
            page = (total - 1) / ListPage;
            (items, total) = await Ctx.Albums.ListAccessible(Ctx.UserId, page * ListPage, ListPage);
        }

        var text = Ctx.T(LocKey.ListTitle);
        if (total == 0) text += Ctx.T(LocKey.ListEmpty) + Ctx.T(LocKey.ListEmptyHint);

        var bf = new ButtonForm();
        foreach (var it in items)
            bf.AddButtonRow($"{RoleIcon(it.Role)} {Text.Truncate(it.Album.Title, 40)} · {Text.N(it.MediaCount)}", $"al:{it.Album.Id}");

        var nav = PageRow(page, total, ListPage, n => $"albums:{n}");
        if (nav != null) bf.AddButtonRow(nav);
        bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.MenuNewAlbum), "new"), new ButtonBase(Ctx.T(LocKey.ButtonMenu), "menu"));

        await Show(m, text, bf);
    }

    public static string RoleIcon(AlbumRole role) => role switch
    {
        AlbumRole.Owner => "👑",
        AlbumRole.Manager => "🛠",
        _ => "📁"
    };

    public string RoleName(AlbumRole role) => Ctx.T(role switch
    {
        AlbumRole.Owner => LocKey.RoleOwner,
        AlbumRole.Manager => LocKey.RoleManager,
        _ => LocKey.RoleMember
    });

    public async Task<Album> OwnedAlbum(MessageResult m, string[] p)
    {
        var album = await Ctx.Albums.GetAlbum(Text.Long(p, 1));
        if (album != null && album.OwnerId == Ctx.UserId) return album;
        await m.ConfirmAction(Ctx.T(LocKey.ErrorAlbumNotOwned));
        return null;
    }

    /// <summary>The album from the callback if the user is its manager or owner.</summary>
    public async Task<Album> ManagedAlbum(MessageResult m, string[] p)
    {
        var album = await Ctx.Albums.GetAlbum(Text.Long(p, 1));
        if (album != null && await Ctx.Albums.CanManage(album, Ctx.UserId)) return album;
        await m.ConfirmAction(Ctx.T(LocKey.ErrorNotAllowed));
        return null;
    }

    public async Task<Album> ViewableAlbum(MessageResult m, string[] p)
    {
        var album = await Ctx.Albums.GetAlbum(Text.Long(p, 1));
        if (album != null && await Ctx.Albums.CanView(album, Ctx.UserId)) return album;
        await m.ConfirmAction(Ctx.T(LocKey.ErrorAlbumUnavailable));
        return null;
    }
}
