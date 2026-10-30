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
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

/// <summary>Admin user list: details, ban / unban and removal of all of a user's data. Authorization is checked on every entry.</summary>
public sealed class AdminUsersScreen : Screen
{
    private const int ListPage = 8;

    public AdminUsersScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "aus", "auu", "aub", "aud", "audok" };

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        if (!Ctx.Config.IsAdmin(Ctx.UserId))
        {
            await m.ConfirmAction(Ctx.T(LocKey.StatsNotAllowed));
            return;
        }

        if (p[0] == "aus")
        {
            await m.ConfirmAction();
            await ShowUsers(m, Text.Int(p, 1));
            return;
        }

        var userId = Text.Long(p, 1);
        var page = Text.Int(p, 2);
        var overview = await Ctx.Users.GetOverviewAsync(userId);
        if (overview == null)
        {
            await m.ConfirmAction(Ctx.T(LocKey.AdminUserGone));
            await ShowUsers(m, page);
            return;
        }

        if (p[0] != "auu" && Ctx.Config.IsAdmin(userId)) // admins can't be banned or wiped, not even by themselves
        {
            await m.ConfirmAction(Ctx.T(LocKey.AdminUserProtected));
            return;
        }

        switch (p[0])
        {
            case "auu":
                await m.ConfirmAction();
                await ShowUser(m, overview, page);
                break;

            case "aub":
            {
                var ban = Text.Int(p, 3) == 1;
                await Ctx.Users.SetBannedAsync(userId, ban);
                await Ctx.Ui.Notify(userId, ban ? LocKey.UserBannedNotice : LocKey.UserUnbannedNotice);
                await m.ConfirmAction(Ctx.T(ban ? LocKey.AdminUserBanned : LocKey.AdminUserUnbanned));
                await ShowUser(m, await Ctx.Users.GetOverviewAsync(userId), page);
                break;
            }

            case "aud":
            {
                await m.ConfirmAction();
                var bf = new ButtonForm();
                bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonConfirmDelete), $"audok:{userId}:{page}"),
                    new ButtonBase(Ctx.T(LocKey.ButtonCancel), $"auu:{userId}:{page}"));
                await Ctx.Ui.Show(m, Ctx.F(LocKey.AdminUserDeleteConfirm, Text.H(Name(overview.User.Name, userId))), bf);
                break;
            }

            case "audok":
            {
                var language = Ctx.Localization.Resolve(await Ctx.Preferences.GetLanguageAsync(userId), null);   // their settings go with the data
                var (albums, media) = await Ctx.Users.DeleteUserDataAsync(userId);
                await Ctx.Ui.Notify(userId, LocKey.UserDataDeletedNotice, language);
                await Ctx.Stats.IncrementAsync(StatKeys.AlbumsDeleted, albums);
                await Ctx.Stats.IncrementAsync(StatKeys.MediaDeleted, media);
                await m.ConfirmAction(Ctx.F(LocKey.AdminUserDeleted, albums, media));
                await ShowUsers(m, page);
                break;
            }
        }
    }

    private string Name(string name, long userId) =>
        string.IsNullOrWhiteSpace(name) ? Ctx.F(LocKey.MemberUnknown, userId) : name;

    private async Task ShowUsers(MessageResult m, int page)
    {
        page = Math.Max(page, 0);
        var (items, total) = await Ctx.Users.ListUsersAsync(page * ListPage, ListPage);
        if (items.Count == 0 && total > 0) // page is out of range after deletions
        {
            page = (total - 1) / ListPage;
            (items, total) = await Ctx.Users.ListUsersAsync(page * ListPage, ListPage);
        }

        var text = Ctx.F(LocKey.AdminUsersTitle, Text.N(total)) + (total == 0 ? Ctx.T(LocKey.ListEmpty) : "");
        var bf = new ButtonForm();
        foreach (var u in items)
        {
            var tag = string.IsNullOrEmpty(u.Username) ? u.UserId.ToString() : "@" + u.Username;
            bf.AddButtonRow($"{(u.IsBanned ? "🚫" : "👤")} {Text.Truncate(Name(u.Name, u.UserId), 30)} · {tag}", $"auu:{u.UserId}:{page}");
        }

        var nav = BotUi.PageRow(page, total, ListPage, n => $"aus:{n}");
        if (nav != null) bf.AddButtonRow(nav);
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), "admin");
        await Ctx.Ui.Show(m, text, bf);
    }

    private async Task ShowUser(MessageResult m, UserOverview o, int page)
    {
        var u = o.User;
        var status = Ctx.T(u.IsBanned ? LocKey.AdminStatusBanned : LocKey.AdminStatusActive);
        var text = Ctx.F(LocKey.AdminUserDetails, u.UserId, Text.H(Name(u.Name, u.UserId)),
            u.FirstSeenAt.ToString("yyyy-MM-dd"), u.LastSeenAt.ToString("yyyy-MM-dd HH:mm"), o.AlbumsOwned, o.AlbumsJoined, status,
            string.IsNullOrEmpty(u.Username) ? "" : "\n@" + Text.H(u.Username));

        var bf = new ButtonForm();
        if (!Ctx.Config.IsAdmin(u.UserId))
        {
            bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonStartConversation), $"auc:{u.UserId}:{page}"),
                new ButtonBase(Ctx.T(u.IsBanned ? LocKey.ButtonUnban : LocKey.ButtonBan), $"aub:{u.UserId}:{page}:{(u.IsBanned ? 0 : 1)}"));
            bf.AddButtonRow(Ctx.T(LocKey.ButtonDeleteUserData), $"aud:{u.UserId}:{page}");
        }

        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), $"aus:{page}");
        await Ctx.Ui.Show(m, text, bf);
    }
}
