using nAlbum.Bot.Ui;
using nAlbum.Services;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

/// <summary>Detailed global statistics. Admins only; authorization is checked on every entry.</summary>
public sealed class StatsScreen : Screen
{
    public StatsScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "stats", "gstats" };

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        if (p[0] == "gstats")
        {
            await m.ConfirmAction();
            await ShowPublic(m);
            return;
        }

        if (!Ctx.Config.IsAdmin(Ctx.UserId))
        {
            await m.ConfirmAction(Ctx.T(LocKey.StatsNotAllowed), true);
            return;
        }

        await m.ConfirmAction();
        await ShowGlobal(m);
    }

    /// <summary>Aggregate statistics for every user; contains no personal data.</summary>
    private async Task ShowPublic(MessageResult m)
    {
        var s = await Ctx.Stats.GetPublicStatsAsync();
        var buttons = new ButtonForm();
        buttons.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonRefresh), "gstats"), new ButtonBase(Ctx.T(LocKey.ButtonMenu), "menu"));
        if (s == null)
        {
            await Ctx.Ui.Show(m, Ctx.T(LocKey.StatsUnavailable), buttons);
            return;
        }

        var text = Ctx.F(
            LocKey.GlobalStatsReport,
            Text.N(s.Users), Text.N(s.Albums), Text.N(s.Media), Text.N(s.Views), Text.N(s.Pages));
        await Ctx.Ui.Show(m, text, buttons);
    }

    /// <summary>Called by the admin-only callback and /stats command.</summary>
    public async Task ShowGlobal(MessageResult m)
    {
        try
        {
            var stats = await Ctx.Stats.GetGlobalStatsAsync();
            var buttons = new ButtonForm();
            buttons.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonRefresh), "stats"), new ButtonBase(Ctx.T(LocKey.ButtonMenu), "menu"));
            await Ctx.Ui.Show(m, Format(stats), buttons);
        }
        catch (Exception ex)
        {
           await Console.Error.WriteLineAsync($"Stats read failed: {ex}");
            await Ctx.Ui.Say(Ctx.T(LocKey.StatsUnavailable));
        }
    }

    private string Format(GlobalStats s)
    {
        var top = s.Top.Count == 0
            ? Ctx.T(LocKey.StatsEmpty)
            : string.Join("\n", s.Top.Select((t, i) =>
                Ctx.F(LocKey.StatsTopRow, i + 1, Text.H(Text.Truncate(t.Title, 30)), Text.N(t.Views), Text.N(t.Pages))));

        return Ctx.F(
            LocKey.StatsReport,
            s.Now, Text.N(s.Users), Text.N(s.ActiveUsers24h), Text.N(s.ActiveUsers7d), Text.N(s.NewUsers7d),
            Text.N(s.Albums), Text.N(s.OpenAlbums), Text.N(s.AlbumsCreated7d), Text.N(s.Media), Text.N(s.Photos),
            Text.N(s.Videos), Text.N(s.MediaAdded7d), s.AvgMediaPerAlbum,
            Text.N(s.LargestAlbum), Text.N(s.ViewerGrants), Text.N(s.CodeJoins), Text.N(s.CodeFailures),
            Text.N(s.CodeThrottled), Text.N(s.ViewsTotal + s.RandomViewsTotal), Text.N(s.ViewsTotal),
            Text.N(s.RandomViewsTotal), Text.N(s.PagesTotal), Text.N(s.AlbumsCreatedTotal), Text.N(s.MediaAddedTotal),
            Text.N(s.AlbumsDeletedTotal), Text.N(s.MediaDeletedTotal), Text.N(s.AlbumsOpenedTimes), top);
    }
}
