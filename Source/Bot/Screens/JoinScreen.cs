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
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

public sealed class JoinScreen : Screen
{
    public JoinScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "leave" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.Idle };
    public override IReadOnlyCollection<string> Commands => new[] { "/join" };

    public override async Task OnCommand(MessageResult m, string command, IReadOnlyList<string> args)
    {
        if (args.Count == 0) await Ctx.Ui.Say(Ctx.T(LocKey.JoinUsage));
        else await TryJoin(args[0]);
    }

    public override async Task OnText(MessageResult m, string text)
    {
        var code = text.ToUpperInvariant();
        if (AlbumService.LooksLikeCode(code))
        {
            await TryJoin(code);
        }
        else
        {
            await Ctx.Ui.Say(Ctx.T(LocKey.JoinHint), Ctx.Ui.MenuButtons());
        }
    }

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        await m.ConfirmAction();
        await Ctx.Albums.Leave(Text.Long(p, 1), Ctx.UserId);
        await Ctx.Ui.ShowList(m, "shared", 0);
    }

    public async Task TryJoin(string raw)
    {
        if (Ctx.Throttle.IsThrottled(Ctx.UserId))
        {
            await Ctx.Stats.IncrementAsync(StatKeys.CodeThrottled);
            await Ctx.Ui.Say(Ctx.T(LocKey.JoinThrottled));
            return;
        }

        var code = raw.Trim().ToUpperInvariant();
        var album = AlbumService.LooksLikeCode(code) ? await Ctx.Albums.JoinByCode(Ctx.UserId, code) : null;
        if (album == null)
        {
            Ctx.Throttle.RegisterFailedAttempt(Ctx.UserId);
            await Ctx.Stats.IncrementAsync(StatKeys.CodeFailures);
            await Ctx.Ui.Say(Ctx.T(LocKey.JoinWrongCode));
            return;
        }

        await Ctx.Stats.IncrementAsync(StatKeys.CodeJoins);
        var (card, bf) = await Ctx.Cards.Build(album);
        await Ctx.Ui.Say(Ctx.T(LocKey.JoinGranted) + card, bf);
    }
}
