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

using System.Text;
using nAlbum.Data;
using TelegramBotBase.Form;
using nAlbum.Localization;

namespace nAlbum.Bot.Ui;

public sealed class AlbumCard
{
    public AlbumCard(BotContext ctx) { Ctx = ctx; }
    private BotContext Ctx { get; }

    public async Task<(string Text, ButtonForm Buttons)> Build(Album album)
    {
        var count = await Ctx.Media.MediaCount(album.Id);
        var watch = await Ctx.Stats.GetAlbumWatchAsync(album.Id);
        var role = await Ctx.Albums.GetRole(album, Ctx.UserId) ?? AlbumRole.Member;
        var manager = role >= AlbumRole.Manager;

        var sb = new StringBuilder();
        sb.Append($"📁 <b>{Text.H(album.Title)}</b>");
        var details = new List<string>();
        if (watch != null) details.Add(Ctx.F(LocKey.CardViewStats, Text.N(watch.Views), Text.N(watch.Pages)));
        var media = Ctx.F(LocKey.CardMedia, count);
        if (manager && album.NextNumber > 1)
            media += Ctx.F(LocKey.CardLastNumber, album.NextNumber - 1);
        details.Add(media);
        sb.Append("\n➖➖➖\n").Append(string.Join("\n", details));
        sb.Append("\n➖➖➖\n").Append(Ctx.F(LocKey.CardRole, Ctx.Ui.RoleName(role)));
        if (manager) sb.Append('\n').Append(Ctx.F(LocKey.CardMembers, await Ctx.Albums.MemberCount(album.Id) + 1));

        var bf = new ButtonForm();
        bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonView), $"view:{album.Id}:0"), new ButtonBase(Ctx.T(LocKey.ButtonRandom), $"rnd:{album.Id}"));
        if (manager) bf.AddButtonRow(Ctx.T(LocKey.ButtonManage), $"mg:{album.Id}");
        var back = new ButtonBase(Ctx.T(LocKey.ButtonBack), role == AlbumRole.Owner ? "mine:0" : "shared:0");
        if (role == AlbumRole.Owner) bf.AddButtonRow(back);
        else bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonLeave), $"leave:{album.Id}"), back);
        return (sb.ToString(), bf);
    }

    /// <summary>Submenu with everything managers and the owner can do.</summary>
    public async Task<(string Text, ButtonForm Buttons)> BuildManage(Album album)
    {
        var role = await Ctx.Albums.GetRole(album, Ctx.UserId) ?? AlbumRole.Member;
        var text = Ctx.F(LocKey.ManageTitle, Text.H(album.Title), Ctx.Ui.RoleName(role));

        var bf = new ButtonForm();
        bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonAddMedia), $"add:{album.Id}"), new ButtonBase(Ctx.T(LocKey.ButtonRemoveMedia), $"rmlist:{album.Id}:0"));
        bf.AddButtonRow(Ctx.T(LocKey.ButtonRename), $"ren:{album.Id}");
        bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonInviteLinks), $"inv:{album.Id}:0"), new ButtonBase(Ctx.T(LocKey.ButtonInviteForward), $"invf:{album.Id}"));
        bf.AddButtonRow(Ctx.T(LocKey.ButtonMembers), $"mem:{album.Id}:0");
        if (role == AlbumRole.Owner)
            bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonTransferOwnership), $"mxf:{album.Id}:0"), new ButtonBase(Ctx.T(LocKey.ButtonDeleteAlbum), $"del:{album.Id}"));
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), $"al:{album.Id}");
        return (text, bf);
    }
}
