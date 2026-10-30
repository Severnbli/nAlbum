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
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

public sealed class MenuScreen : Screen
{
    public MenuScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "menu", "albums", "language", "lang", "help", "privacy", "pgend" };
    public override IReadOnlyCollection<string> Commands => new[] { "/cancel", "/menu", "/albums", "/language", "/help", "/privacy" };

    public Task<string> WelcomeTextAsync() => Task.FromResult(Ctx.T(LocKey.MenuWelcome));

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
            await Ctx.Ui.ShowList(m, 0);
            return;
        }

        if (command == "/help")
        {
            await ShowHelp(m);
            return;
        }

        if (command == "/privacy")
        {
            await ShowPrivacy(m);
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

            case "albums":
                Ctx.Session.Mode = Mode.Idle;
                await m.ConfirmAction();
                await Ctx.Ui.ShowList(m, Text.Int(p, 1));
                break;

            case "help":
                await m.ConfirmAction();
                await ShowHelp(m);
                break;

            case "pgend":
                await m.ConfirmAction(Ctx.T(p.Length > 1 && p[1] == "first" ? LocKey.PageFirst : LocKey.PageLast));
                break;

            case "privacy":
                await m.ConfirmAction();
                await ShowPrivacy(m);
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

    private async Task ShowHelp(MessageResult m)
    {
        var bf = new ButtonForm();
        if (!Ctx.Config.IsAdmin(Ctx.UserId) && Ctx.Config.AdminIds.Count > 0)
            bf.AddButtonRow(Ctx.T(LocKey.ButtonSupport), "sup");
        bf.AddButtonRow(Ctx.T(LocKey.ButtonMenu), "menu");
        await Ctx.Ui.Show(m, Ctx.T(LocKey.HelpText), bf);
    }

    private async Task ShowPrivacy(MessageResult m)
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(Ctx.T(LocKey.ButtonMenu), "menu");
        await Ctx.Ui.Show(m, Ctx.T(LocKey.PrivacyPolicy), bf);
    }

    private async Task ShowLanguage(MessageResult m)
    {
        await Ctx.Ui.Show(m, Ctx.T(LocKey.LanguageChoose), await LanguageButtons());
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
        await Ctx.Ui.Show(m, $"{note}\n\n{Ctx.T(LocKey.LanguageChoose)}", await LanguageButtons());
    }

    /// <summary>Two languages per row; the saved choice (or auto-detect) is marked with ✅.</summary>
    private async Task<ButtonForm> LanguageButtons()
    {
        var preference = await Ctx.Preferences.GetLanguageAsync(Ctx.UserId);
        var current = Ctx.Localization.Has(preference) ? preference : null;
        string Mark(string label, bool selected) => selected ? $"✅ {label}" : label;

        var buttons = new ButtonForm();
        foreach (var pair in Ctx.Localization.Languages.Chunk(2))
            buttons.AddButtonRow(pair.Select(l => new ButtonBase(Mark(l.Label, l.Code == current), $"lang:{l.Code}")).ToArray());
        buttons.AddButtonRow(new ButtonBase(Mark(Ctx.T(LocKey.LanguageAutoDetect), current == null), "lang:auto"));
        buttons.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonMenu), "menu"));
        return buttons;
    }
}
