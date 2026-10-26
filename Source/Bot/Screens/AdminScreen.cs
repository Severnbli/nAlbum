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
using nAlbum.Services;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using TelegramBotBase.Sessions;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

/// <summary>Admin panel: detailed statistics and removal of any album. Authorization is checked on every entry.</summary>
public sealed class AdminScreen : Screen
{
    private const int ListPage = 8;
    private static readonly SemaphoreSlim BroadcastGate = new(1, 1);                  // one broadcast per process

    public AdminScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "admin", "aal", "aa", "adel", "adelok", "abc", "abcok" };
    public override IReadOnlyCollection<string> Commands => new[] { "/admin" };

    public override async Task OnCommand(MessageResult m, string command, IReadOnlyList<string> args)
    {
        if (!Ctx.Config.IsAdmin(Ctx.UserId))
        {
            await Ctx.Ui.Say(Ctx.T(LocKey.StatsNotAllowed));
            return;
        }

        await ShowPanel(m);
    }

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        if (!Ctx.Config.IsAdmin(Ctx.UserId))
        {
            await m.ConfirmAction(Ctx.T(LocKey.StatsNotAllowed), true);
            return;
        }

        if (Ctx.Session.Mode == Mode.Broadcast) Ctx.Session.Mode = Mode.Idle;
        switch (p[0])
        {
            case "admin":
                await m.ConfirmAction();
                await ShowPanel(m);
                break;

            case "abc":
                await m.ConfirmAction();
                Ctx.Session.Mode = Mode.Broadcast;
                Ctx.Session.BroadcastMessageId = 0;
                await Ctx.Ui.Show(m, Ctx.T(LocKey.BroadcastPrompt), BackButtons());
                break;

            case "abcok":
                await StartBroadcast(m, Text.Int(p, 1));
                break;

            case "aal":
                await m.ConfirmAction();
                await ShowAlbums(m, Text.Int(p, 1));
                break;

            case "aa":
            case "adel":
            {
                var album = await Ctx.Albums.GetAlbum(Text.Long(p, 1));
                if (album == null)
                {
                    await m.ConfirmAction(Ctx.T(LocKey.ErrorAlbumUnavailable), true);
                    return;
                }

                await m.ConfirmAction();
                var page = Text.Int(p, 2);
                var bf = new ButtonForm();
                if (p[0] == "aa")
                {
                    var media = await Ctx.Media.MediaCount(album.Id);
                    var members = await Ctx.Albums.MemberCount(album.Id);
                    bf.AddButtonRow(Ctx.T(LocKey.ButtonDeleteAlbum), $"adel:{album.Id}:{page}");
                    bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), $"aal:{page}");
                    await Ctx.Ui.Show(m, Ctx.F(LocKey.AdminAlbumDetails, album.Id, Text.H(album.Title),
                        album.CreatedAt.ToString("yyyy-MM-dd"), media, members), bf);
                }
                else
                {
                    bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonConfirmDelete), $"adelok:{album.Id}:{page}"),
                        new ButtonBase(Ctx.T(LocKey.ButtonCancel), $"aa:{album.Id}:{page}"));
                    await Ctx.Ui.Show(m, Ctx.F(LocKey.AlbumDeleteConfirm, Text.H(album.Title)), bf);
                }
                break;
            }

            case "adelok":
            {
                var album = await Ctx.Albums.GetAlbum(Text.Long(p, 1));
                if (album == null)
                {
                    await m.ConfirmAction(Ctx.T(LocKey.ErrorAlbumUnavailable), true);
                    return;
                }

                await m.ConfirmAction(Ctx.T(LocKey.AlbumDeleted));
                var media = await Ctx.Albums.DeleteAlbum(album.Id);
                await Ctx.Stats.IncrementAsync(StatKeys.AlbumsDeleted);
                await Ctx.Stats.IncrementAsync(StatKeys.MediaDeleted, media);
                await ShowAlbums(m, Text.Int(p, 2));
                break;
            }
        }
    }

    private async Task ShowPanel(MessageResult m)
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(Ctx.T(LocKey.MenuDetailedStatistics), "stats");
        bf.AddButtonRow(Ctx.T(LocKey.AdminRemoveAlbums), "aal:0");
        bf.AddButtonRow(Ctx.T(LocKey.AdminBroadcast), "abc");
        bf.AddButtonRow(Ctx.T(LocKey.ButtonMenu), "menu");
        await Ctx.Ui.Show(m, Ctx.T(LocKey.AdminTitle), bf);
    }

    private async Task ShowAlbums(MessageResult m, int page)
    {
        page = Math.Max(page, 0);
        var (items, total) = await Ctx.Albums.ListAll(page * ListPage, ListPage);
        if (items.Count == 0 && total > 0) // page is out of range after deletions
        {
            page = (total - 1) / ListPage;
            (items, total) = await Ctx.Albums.ListAll(page * ListPage, ListPage);
        }

        var text = Ctx.T(LocKey.AdminAlbumsTitle) + (total == 0 ? Ctx.T(LocKey.ListEmpty) : "");
        var bf = new ButtonForm();
        foreach (var it in items)
            bf.AddButtonRow($"#{it.Album.Id} {Text.Truncate(it.Album.Title, 36)} ({it.MediaCount})", $"aa:{it.Album.Id}:{page}");

        var nav = BotUi.PageRow(page, total, ListPage, n => $"aal:{n}");
        if (nav != null) bf.AddButtonRow(nav);
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), "admin");
        await Ctx.Ui.Show(m, text, bf);
    }

    // ---------------------------------------------------------- broadcast
    private ButtonForm BackButtons()
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(Ctx.T(LocKey.ButtonCancelAction), "admin");
        return bf;
    }

    /// <summary>Any message in Mode.Broadcast: it stays in the chat as the preview and waits for confirmation.</summary>
    public async Task OnBroadcastMessage(Message msg)
    {
        if (msg == null) return;
        if (!Ctx.Config.IsAdmin(Ctx.UserId))
        {
            Ctx.Session.Mode = Mode.Idle;
            return;
        }

        if (msg.MediaGroupId != null)
        {
            await Ctx.Ui.TryDelete(msg.MessageId);
            await Ctx.Ui.Say(Ctx.T(LocKey.BroadcastNoGroups), BackButtons());
            return;
        }

        Ctx.Session.Mode = Mode.Idle;
        Ctx.Session.BroadcastMessageId = msg.MessageId;
        var recipients = (await Ctx.Stats.ListUserIdsAsync()).Count(id => id != Ctx.UserId);
        var bf = new ButtonForm();
        bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonSend), $"abcok:{msg.MessageId}"), new ButtonBase(Ctx.T(LocKey.ButtonCancelAction), "admin"));
        await Ctx.Ui.Say(Ctx.F(LocKey.BroadcastConfirm, Text.N(recipients)), bf);
    }

    private async Task StartBroadcast(MessageResult m, int messageId)
    {
        if (messageId == 0 || messageId != Ctx.Session.BroadcastMessageId)
        {
            await m.ConfirmAction(Ctx.T(LocKey.BroadcastExpired), true);
            return;
        }

        if (!await BroadcastGate.WaitAsync(0))
        {
            await m.ConfirmAction(Ctx.T(LocKey.BroadcastBusy), true);
            return;
        }

        Ctx.Session.BroadcastMessageId = 0;
        await m.ConfirmAction();
        var recipients = (await Ctx.Stats.ListUserIdsAsync()).Where(id => id != Ctx.UserId).ToList();
        var bf = new ButtonForm();
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), "admin");
        await Ctx.Ui.Show(m, Ctx.F(LocKey.BroadcastStarted, Text.N(recipients.Count)), bf);

        var adminId = Ctx.UserId;
        var device = Ctx.Device;
        var language = Ctx.Language;
        _ = Task.Run(async () =>
        {
            try
            {
                var (sent, failed) = await Broadcast(device, adminId, messageId, recipients);
                await device.Dispatch(a => a.SendMessage(adminId,
                    Ctx.Localization.F(language, LocKey.BroadcastDone, Text.N(sent), Text.N(failed)),
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Html));
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Broadcast failed: {ex}");
            }
            finally
            {
                BroadcastGate.Release();
            }
        });
    }

    /// <summary>Copies the message to every recipient (the framework's dispatcher applies rate limits). Errors count as failed.</summary>
    private static async Task<(int Sent, int Failed)> Broadcast(TelegramBotBase.Interfaces.IDeviceSession device,
        long fromChat, int messageId, List<long> recipients)
    {
        int sent = 0, failed = 0;
        foreach (var userId in recipients)
        {
            try
            {
                await device.Dispatch(a => a.CopyMessage(userId, fromChat, messageId));
                sent++;
            }
            catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
            {
                failed++; // blocked the bot, deleted account, ...
            }
        }

        return (sent, failed);
    }
}
