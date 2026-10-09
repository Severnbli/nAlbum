using nAlbum.Bot.Ui;
using TelegramBotBase.Base;

namespace nAlbum.Bot.Screens;

public sealed class MenuScreen : Screen
{
    public const string Welcome =
        "👋 <b>Albums bot</b>\n\n" +
        "Create albums of photos and videos and share them with a secret code.\n" +
        "Got a code from a friend? Just send it to me.";

    public MenuScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "menu", "mine", "shared" };
    public override IReadOnlyCollection<string> Commands => new[] { "/cancel", "/menu", "/albums" };

    public async Task ShowWelcome() => await Ctx.Ui.Say(Welcome, Ctx.Ui.MenuButtons());

    public override async Task OnCommand(MessageResult m, string command, IReadOnlyList<string> args)
    {
        if (command == "/cancel")
        {
            await Ctx.Ui.Say("Cancelled.", Ctx.Ui.MenuButtons());
            return;
        }

        if (command == "/albums")
        {
            await Ctx.Ui.ShowList(m, "mine", 0);
            return;
        }

        await ShowWelcome();
    }

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        switch (p[0])
        {
            case "menu":
                Ctx.Session.Mode = Mode.Idle;
                await m.ConfirmAction();
                await Ctx.Ui.Show(m, Welcome, Ctx.Ui.MenuButtons());
                break;

            case "mine":
            case "shared":
                Ctx.Session.Mode = Mode.Idle;
                await m.ConfirmAction();
                await Ctx.Ui.ShowList(m, p[0], Text.Int(p, 1));
                break;
        }
    }
}
