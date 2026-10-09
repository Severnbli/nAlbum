using nAlbum.Bot.Ui;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

public sealed class MenuScreen : Screen
{
    public MenuScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "menu", "mine", "shared", "language", "lang" };
    public override IReadOnlyCollection<string> Commands => new[] { "/cancel", "/menu", "/albums", "/language" };

    public async Task<string> WelcomeTextAsync()
    {
        var s = await Ctx.Stats.GetPublicStatsAsync();
        var welcome = Ctx.T(LocKey.MenuWelcome);
        return s == null
            ? welcome
            : $"{welcome}\n\n{Ctx.F(LocKey.MenuPublicStats, Text.N(s.Albums), Text.N(s.Views), Text.N(s.Pages))}";
    }

    public async Task ShowWelcome() => await Ctx.Ui.Say(await WelcomeTextAsync(), Ctx.Ui.MenuButtons());

    public override async Task OnCommand(MessageResult m, string command, IReadOnlyList<string> args)
    {
        if (command == "/cancel")
        {
            await Ctx.Ui.Say(Ctx.T(LocKey.Cancelled), Ctx.Ui.MenuButtons());
            return;
        }

        if (command == "/albums")
        {
            await Ctx.Ui.ShowList(m, "mine", 0);
            return;
        }

        if (command == "/language")
        {
            await ShowLanguage(m);
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
                await Ctx.Ui.Show(m, await WelcomeTextAsync(), Ctx.Ui.MenuButtons());
                break;

            case "mine":
            case "shared":
                Ctx.Session.Mode = Mode.Idle;
                await m.ConfirmAction();
                await Ctx.Ui.ShowList(m, p[0], Text.Int(p, 1));
                break;

            case "language":
                await m.ConfirmAction();
                await ShowLanguage(m);
                break;

            case "lang":
                await SetLanguage(m, p);
                break;
        }
    }

    private async Task ShowLanguage(MessageResult m)
    {
        await Ctx.Ui.Show(m, Ctx.T(LocKey.LanguageChoose), LanguageButtons());
    }

    private async Task SetLanguage(MessageResult m, string[] p)
    {
        var code = p.Length > 1 ? p[1] : "";
        if (code != "auto" && !Ctx.Localization.Has(code))
        {
            await m.ConfirmAction();
            await ShowLanguage(m);
            return;
        }

        await Ctx.Preferences.SetLanguageAsync(Ctx.UserId, code == "auto" ? null : code);
        Ctx.Language = code == "auto"
            ? Ctx.Localization.Detect(m.UpdateData.CallbackQuery?.From?.LanguageCode)
            : code;
        await m.ConfirmAction();
        var note = code == "auto" ? Ctx.T(LocKey.LanguageAutoEnabled) : Ctx.T(LocKey.LanguageUpdated);
        await Ctx.Ui.Show(m, $"{note}\n\n{Ctx.T(LocKey.LanguageChoose)}", LanguageButtons());
    }

    private ButtonForm LanguageButtons()
    {
        var buttons = new ButtonForm();
        foreach (var language in Ctx.Localization.Languages)
            buttons.AddButtonRow(new ButtonBase(language.Label, $"lang:{language.Code}"));
        buttons.AddButtonRow(new ButtonBase(Ctx.T(LocKey.LanguageAutoDetect), "lang:auto"));
        buttons.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonMenu), "menu"));
        return buttons;
    }
}
