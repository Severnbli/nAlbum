using nAlbum.Bot.Ui;
using nAlbum.Services;
using TelegramBotBase.Base;

namespace nAlbum.Bot.Screens;

public sealed class JoinScreen : Screen
{
    public JoinScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "leave" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.Idle };
    public override IReadOnlyCollection<string> Commands => new[] { "/join" };

    public override async Task OnCommand(MessageResult m, string command, IReadOnlyList<string> args)
    {
        if (args.Count == 0) await Ctx.Ui.Say(Ctx.T("Usage: /join CODE"));
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
            await Ctx.Ui.Say(Ctx.T("Send me an access code to open an album, or use the menu."), Ctx.Ui.MenuButtons());
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
            await Ctx.Ui.Say(Ctx.T("⏳ Too many wrong codes. Please try again in a few minutes."));
            return;
        }

        var code = raw.Trim().ToUpperInvariant();
        var album = AlbumService.LooksLikeCode(code) ? await Ctx.Albums.JoinByCode(Ctx.UserId, code) : null;
        if (album == null)
        {
            Ctx.Throttle.RegisterFailedAttempt(Ctx.UserId);
            await Ctx.Stats.IncrementAsync(StatKeys.CodeFailures);
            await Ctx.Ui.Say(Ctx.T("❌ Wrong code, or the album is closed."));
            return;
        }

        await Ctx.Stats.IncrementAsync(StatKeys.CodeJoins);
        var (card, bf) = await Ctx.Cards.Build(album);
        await Ctx.Ui.Say(Ctx.T("✅ Access granted.\n\n") + card, bf);
    }
}
