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
using nAlbum.Services;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using TelegramBotBase.Sessions;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

/// <summary>
/// Members, roles, ownership transfer and invitations. Managers rule members; only the owner rules managers.
/// Every callback re-checks the actor's and the target's roles on the server.
/// </summary>
public sealed class MembersScreen : Screen
{
    private const int ListPage = 8;
    private const int InvitePage = 5;
    private static readonly int[] LifetimeHours = { 1, 24, 24 * 7, 24 * 30, 0 };   // 0 = no limit
    private static readonly int[] UseLimits = { 1, 5, 10, 50, 0 };                 // 0 = no limit

    public MembersScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[]
    {
        "mem", "mu", "mk", "mkok", "mpr", "mde", "mxf", "mto", "mtook",
        "inv", "invn", "inve", "invc", "invd", "invf"
    };
    public override async Task OnCallback(MessageResult m, string[] p)
    {
        if (p[0] != "invf" && Ctx.Session.Mode == Mode.InviteForward) Ctx.Session.Mode = Mode.Idle;
        var album = p[0] is "mxf" or "mto" or "mtook" ? await Ctx.Ui.OwnedAlbum(m, p) : await Ctx.Ui.ManagedAlbum(m, p);
        if (album == null) return;
        var role = album.OwnerId == Ctx.UserId ? AlbumRole.Owner : AlbumRole.Manager;

        switch (p[0])
        {
            case "mem":
                await m.ConfirmAction();
                await ShowMembers(m, album, Text.Int(p, 2), false);
                break;

            case "mxf":
                await m.ConfirmAction();
                await ShowMembers(m, album, Text.Int(p, 2), true);
                break;

            case "mu":
            case "mk":
            case "mkok":
            case "mpr":
            case "mde":
            case "mto":
            case "mtook":
                await OnMember(m, p, album, role);
                break;

            case "inv":
                await m.ConfirmAction();
                await ShowInvites(m, album, Text.Int(p, 2), null);
                break;

            case "invn":
            {
                await m.ConfirmAction();
                var bf = new ButtonForm();
                bf.AddButtonRow(LifetimeHours.Select(h => new ButtonBase(LifetimeLabel(h), $"inve:{album.Id}:{h}")).ToArray());
                bf.AddButtonRow(Ctx.T(LocKey.ButtonCancelAction), $"inv:{album.Id}:0");
                await Ctx.Ui.Show(m, Ctx.T(LocKey.InviteChooseExpiry), bf);
                break;
            }

            case "inve":
            {
                await m.ConfirmAction();
                var hours = Math.Max(Text.Int(p, 2), 0);
                var bf = new ButtonForm();
                bf.AddButtonRow(UseLimits.Select(u => new ButtonBase(u == 0 ? Ctx.T(LocKey.InviteNoLimit) : u.ToString(),
                    $"invc:{album.Id}:{hours}:{u}")).ToArray());
                bf.AddButtonRow(Ctx.T(LocKey.ButtonCancelAction), $"inv:{album.Id}:0");
                await Ctx.Ui.Show(m, Ctx.T(LocKey.InviteChooseUses), bf);
                break;
            }

            case "invc":
            {
                var hours = Text.Int(p, 2);
                var uses = Text.Int(p, 3);
                if (!LifetimeHours.Contains(hours) || !UseLimits.Contains(uses))
                {
                    await m.ConfirmAction();
                    return;
                }

                await m.ConfirmAction();
                var link = await Ctx.Albums.CreateInvite(album.Id, Ctx.UserId,
                    hours == 0 ? null : TimeSpan.FromHours(hours), uses == 0 ? null : uses);
                await Ctx.Stats.IncrementAsync(StatKeys.InvitesCreated);
                var bf = new ButtonForm();
                bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonInviteLinks), $"inv:{album.Id}:0"), new ButtonBase(Ctx.T(LocKey.ButtonBack), $"mg:{album.Id}"));
                await Ctx.Ui.Show(m, Ctx.F(LocKey.InviteCreated, Text.H(album.Title), InviteUrl(link),
                    ExpiryText(link), UsesText(link)), bf);
                break;
            }

            case "invd":
            {
                var deleted = await Ctx.Albums.DeleteInvite(album.Id, Text.Long(p, 2));
                await m.ConfirmAction(deleted ? Ctx.T(LocKey.InviteDeleted) : null);
                await ShowInvites(m, album, Text.Int(p, 3), null);
                break;
            }

            case "invf":
            {
                await m.ConfirmAction();
                Ctx.Session.Mode = Mode.InviteForward;
                Ctx.Session.AlbumId = album.Id;
                await Ctx.Ui.Show(m, Ctx.F(LocKey.InviteForwardPrompt, Text.H(album.Title)), ForwardButtons(album, false));
                break;
            }
        }
    }

    // ------------------------------------------------------------ members
    private async Task ShowMembers(MessageResult m, Album album, int page, bool transfer)
    {
        var members = await Ctx.Albums.ListMembers(album);
        if (transfer) members = members.Where(x => x.Role != AlbumRole.Owner).ToList();
        var total = members.Count;
        page = Math.Clamp(page, 0, Math.Max(total - 1, 0) / ListPage);

        var text = transfer
            ? Ctx.F(LocKey.TransferPick, Text.H(album.Title)) + (total == 0 ? Ctx.T(LocKey.TransferNoMembers) : "")
            : Ctx.F(LocKey.MembersTitle, Text.H(album.Title), total);

        var bf = new ButtonForm();
        foreach (var x in members.Skip(page * ListPage).Take(ListPage))
        {
            var label = $"{BotUi.RoleIcon(x.Role)} {Text.Truncate(MemberName(x), 40)}";
            bf.AddButtonRow(label, transfer ? $"mto:{album.Id}:{x.UserId}" : $"mu:{album.Id}:{x.UserId}:{page}");
        }

        var cb = transfer ? "mxf" : "mem";
        var nav = BotUi.PageRow(page, total, ListPage, n => $"{cb}:{album.Id}:{n}");
        if (nav != null) bf.AddButtonRow(nav);
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), $"mg:{album.Id}");
        await Ctx.Ui.Show(m, text, bf);
    }

    private string MemberName(MemberInfo x)
    {
        var name = string.IsNullOrWhiteSpace(x.Name) ? Ctx.F(LocKey.MemberUnknown, x.UserId) : x.Name;
        return x.UserId == Ctx.UserId ? name + Ctx.T(LocKey.MemberYou) : name;
    }

    /// <summary>Managers may remove members; the owner may also remove managers and change roles.</summary>
    private static bool CanKick(AlbumRole actor, AlbumRole target) =>
        target != AlbumRole.Owner && actor > target;

    private async Task OnMember(MessageResult m, string[] p, Album album, AlbumRole actor)
    {
        var userId = Text.Long(p, 2);
        var page = Text.Int(p, 3);
        var target = (await Ctx.Albums.ListMembers(album)).FirstOrDefault(x => x.UserId == userId);
        if (target == null)
        {
            await m.ConfirmAction(Ctx.T(LocKey.MemberGone), true);
            await ShowMembers(m, album, page, p[0] is "mto" or "mtook");
            return;
        }

        var name = Text.H(MemberName(target));
        switch (p[0])
        {
            case "mk":
            case "mkok":
                if (!CanKick(actor, target.Role) || target.UserId == Ctx.UserId) break;
                if (p[0] == "mk")
                {
                    await m.ConfirmAction();
                    var bf = new ButtonForm();
                    bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonKick), $"mkok:{album.Id}:{userId}:{page}"),
                        new ButtonBase(Ctx.T(LocKey.ButtonCancel), $"mu:{album.Id}:{userId}:{page}"));
                    await Ctx.Ui.Show(m, Ctx.F(LocKey.MemberKickConfirm, name, Text.H(album.Title)), bf);
                    return;
                }

                await Ctx.Albums.Leave(album.Id, userId);
                await m.ConfirmAction(Ctx.T(LocKey.MemberKicked));
                await ShowMembers(m, album, page, false);
                return;

            case "mpr":
            case "mde":
                if (actor != AlbumRole.Owner || target.Role == AlbumRole.Owner) break;
                var promote = p[0] == "mpr";
                await Ctx.Albums.SetRole(album.Id, userId, promote ? AlbumRole.Manager : AlbumRole.Member);
                await m.ConfirmAction(Ctx.T(promote ? LocKey.MemberPromoted : LocKey.MemberDemoted));
                target.Role = promote ? AlbumRole.Manager : AlbumRole.Member;
                break;

            case "mto":
            {
                if (target.Role == AlbumRole.Owner) break;
                await m.ConfirmAction();
                var bf = new ButtonForm();
                bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonMakeOwner), $"mtook:{album.Id}:{userId}"),
                    new ButtonBase(Ctx.T(LocKey.ButtonCancel), $"mg:{album.Id}"));
                await Ctx.Ui.Show(m, Ctx.F(LocKey.TransferConfirm, name, Text.H(album.Title)), bf);
                return;
            }

            case "mtook":
            {
                if (target.Role == AlbumRole.Owner || !await Ctx.Albums.TransferOwnership(album.Id, Ctx.UserId, userId))
                {
                    await m.ConfirmAction(Ctx.T(LocKey.MemberGone), true);
                    return;
                }

                await m.ConfirmAction(Ctx.T(LocKey.TransferDone), true);
                album = await Ctx.Albums.GetAlbum(album.Id);
                var (card, cardButtons) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Show(m, card, cardButtons);
                return;
            }

            default:
                await m.ConfirmAction();
                break;
        }

        await ShowMember(m, album, actor, target, page);
    }

    private async Task ShowMember(MessageResult m, Album album, AlbumRole actor, MemberInfo target, int page)
    {
        var bf = new ButtonForm();
        var self = target.UserId == Ctx.UserId;
        if (!self && actor == AlbumRole.Owner && target.Role == AlbumRole.Member)
            bf.AddButtonRow(Ctx.T(LocKey.ButtonPromote), $"mpr:{album.Id}:{target.UserId}:{page}");
        if (!self && actor == AlbumRole.Owner && target.Role == AlbumRole.Manager)
            bf.AddButtonRow(Ctx.T(LocKey.ButtonDemote), $"mde:{album.Id}:{target.UserId}:{page}");
        if (!self && actor == AlbumRole.Owner && target.Role != AlbumRole.Owner)
            bf.AddButtonRow(Ctx.T(LocKey.ButtonMakeOwner), $"mto:{album.Id}:{target.UserId}");
        if (!self && CanKick(actor, target.Role))
            bf.AddButtonRow(Ctx.T(LocKey.ButtonKick), $"mk:{album.Id}:{target.UserId}:{page}");
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), $"mem:{album.Id}:{page}");

        await Ctx.Ui.Show(m, Ctx.F(LocKey.MemberDetails, Text.H(MemberName(target)), Ctx.Ui.RoleName(target.Role),
            Text.H(album.Title)), bf);
    }

    // ------------------------------------------------------- invite links
    private async Task ShowInvites(MessageResult m, Album album, int page, string note)
    {
        page = Math.Max(page, 0);
        var (items, total) = await Ctx.Albums.ListInvites(album.Id, page * InvitePage, InvitePage);
        if (items.Count == 0 && total > 0) // page is out of range after deletions
        {
            page = (total - 1) / InvitePage;
            (items, total) = await Ctx.Albums.ListInvites(album.Id, page * InvitePage, InvitePage);
        }

        var text = Ctx.F(LocKey.InvitesTitle, Text.H(album.Title));
        if (total == 0) text += Ctx.T(LocKey.InvitesEmpty);
        var bf = new ButtonForm();
        var deleteRow = new List<ButtonBase>();
        for (var i = 0; i < items.Count; i++)
        {
            var link = items[i];
            var n = page * InvitePage + i + 1;
            text += "\n\n" + Ctx.F(LocKey.InviteRow, n, InviteUrl(link), ExpiryText(link), UsesText(link));
            deleteRow.Add(new ButtonBase(Ctx.F(LocKey.ButtonDeleteInvite, n), $"invd:{album.Id}:{link.Id}:{page}"));
        }

        if (note != null) text = note + "\n\n" + text;
        if (deleteRow.Count > 0) bf.AddButtonRow(deleteRow.ToArray());
        var nav = BotUi.PageRow(page, total, InvitePage, n => $"inv:{album.Id}:{n}");
        if (nav != null) bf.AddButtonRow(nav);
        bf.AddButtonRow(Ctx.T(LocKey.ButtonNewInvite), $"invn:{album.Id}");
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), $"mg:{album.Id}");
        await Ctx.Ui.Show(m, text, bf);
    }

    private static string InviteUrl(InviteLink link) => $"https://t.me/{BotInfo.Username}?start={link.Code}";

    private string LifetimeLabel(int hours) => hours switch
    {
        0 => Ctx.T(LocKey.InviteNoLimit),
        < 24 => Ctx.F(LocKey.InviteHours, hours),
        _ => Ctx.F(LocKey.InviteDays, hours / 24)
    };

    private string ExpiryText(InviteLink link)
    {
        if (link.ExpiresAt == null) return Ctx.T(LocKey.InviteNeverExpires);
        return link.ExpiresAt <= DateTime.UtcNow
            ? Ctx.T(LocKey.InviteExpired)
            : Ctx.F(LocKey.InviteExpiresAt, link.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm"));
    }

    private string UsesText(InviteLink link) =>
        link.MaxUses == null ? Ctx.F(LocKey.InviteUsesUnlimited, link.Uses) : Ctx.F(LocKey.InviteUses, link.Uses, link.MaxUses);

    // ------------------------------------------------- invite by forward
    private ButtonForm ForwardButtons(Album album, bool done)
    {
        var bf = new ButtonForm();
        if (done) bf.AddButtonRow(Ctx.T(LocKey.ButtonInviteAnother), $"invf:{album.Id}");
        bf.AddButtonRow(Ctx.T(done ? LocKey.ButtonBack : LocKey.ButtonCancelAction), $"mg:{album.Id}");
        return bf;
    }

    /// <summary>Any message sent in Mode.InviteForward. A message forwarded from a person adds them as a member.</summary>
    public async Task OnForward(Message msg)
    {
        if (msg == null) return;
        var album = await Ctx.Albums.GetAlbum(Ctx.Session.AlbumId);
        if (album == null || !await Ctx.Albums.CanManage(album, Ctx.UserId))
        {
            Ctx.Session.Mode = Mode.Idle;
            return;
        }

        await Ctx.Ui.TryDelete(msg.MessageId); // keep the chat tidy
        var prompt = Ctx.F(LocKey.InviteForwardPrompt, Text.H(album.Title));
        switch (msg.ForwardOrigin)
        {
            case MessageOriginHiddenUser:
                await Ctx.Ui.ShowMenu(Ctx.T(LocKey.InviteForwardHidden) + "\n\n" + prompt, ForwardButtons(album, false));
                return;

            case MessageOriginUser { SenderUser.IsBot: true }:
                await Ctx.Ui.ShowMenu(Ctx.T(LocKey.InviteForwardBot) + "\n\n" + prompt, ForwardButtons(album, false));
                return;

            case MessageOriginUser origin:
            {
                var user = origin.SenderUser;
                var name = Text.UserName(user) ?? Ctx.F(LocKey.MemberUnknown, user.Id);
                await Ctx.Stats.RefreshNameAsync(user.Id, Text.UserName(user));
                if (!await Ctx.Albums.AddMember(album.Id, user.Id))
                {
                    await Ctx.Ui.ShowMenu(Ctx.F(LocKey.InviteForwardAlready, Text.H(name)) + "\n\n" + prompt,
                        ForwardButtons(album, false));
                    return;
                }

                Ctx.Session.Mode = Mode.Idle;
                await Ctx.Stats.IncrementAsync(StatKeys.ForwardInvites);
                var notified = await NotifyAdded(album, user.Id);
                var text = Ctx.F(LocKey.InviteForwardAdded, Text.H(name), Text.H(album.Title))
                           + Ctx.T(notified ? LocKey.InviteForwardNotified : LocKey.InviteForwardNotNotified);
                await Ctx.Ui.ShowMenu(text, ForwardButtons(album, true));
                return;
            }

            default:
                await Ctx.Ui.ShowMenu(Ctx.T(LocKey.InviteForwardNotForwarded) + "\n\n" + prompt, ForwardButtons(album, false));
                return;
        }
    }

    /// <summary>Tells the invited user, in their own language. Fails when they have never started the bot.</summary>
    private async Task<bool> NotifyAdded(Album album, long userId)
    {
        var language = Ctx.Localization.Resolve(await Ctx.Preferences.GetLanguageAsync(userId), null);
        var bf = new ButtonForm();
        bf.AddButtonRow(Ctx.Localization.T(language, LocKey.ButtonOpen), $"al:{album.Id}");
        try
        {
            await Ctx.Device.Dispatch(a => a.SendMessage(userId,
                Ctx.Localization.F(language, LocKey.AddedToAlbumNotice, Text.H(album.Title)),
                parseMode: ParseMode.Html, replyMarkup: new InlineKeyboardMarkup(bf.ToInlineButtonArray())));
            return true;
        }
        catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Could not notify invited user: {ex.Message}");
            return false;
        }
    }
}
