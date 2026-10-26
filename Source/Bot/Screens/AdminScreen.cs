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

/// <summary>Admin panel: detailed statistics and removal of any album. Authorization is checked on every entry.</summary>
public sealed class AdminScreen : Screen
{
    private const int ListPage = 8;

    public AdminScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "admin", "aal", "aa", "adel", "adelok" };
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

        switch (p[0])
        {
            case "admin":
                await m.ConfirmAction();
                await ShowPanel(m);
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

        var nav = new List<ButtonBase>();
        if (page > 0) nav.Add(new ButtonBase("◀", $"aal:{page - 1}"));
        if ((page + 1) * ListPage < total) nav.Add(new ButtonBase("▶", $"aal:{page + 1}"));
        if (nav.Count > 0) bf.AddButtonRow(nav.ToArray());
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), "admin");
        await Ctx.Ui.Show(m, text, bf);
    }
}
