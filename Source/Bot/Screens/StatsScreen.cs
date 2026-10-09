using nAlbum.Bot.Ui;
using nAlbum.Services;
using TelegramBotBase.Base;
using TelegramBotBase.Form;

namespace nAlbum.Bot.Screens;

/// <summary>Detailed global statistics. Admins only; authorization is checked on every entry.</summary>
public sealed class StatsScreen : Screen
{
    public StatsScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "stats" };

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        if (!Ctx.Config.IsAdmin(Ctx.UserId))
        {
            await m.ConfirmAction(Ctx.T("Not allowed."), true);
            return;
        }

        await m.ConfirmAction();
        await ShowGlobal(m);
    }

    /// <summary>Called by the admin-only callback and /stats command.</summary>
    public async Task ShowGlobal(MessageResult m)
    {
        try
        {
            var stats = await Ctx.Stats.GetGlobalStatsAsync();
            var buttons = new ButtonForm();
            buttons.AddButtonRow(new ButtonBase(Ctx.T("🔄 Refresh"), "stats"), new ButtonBase(Ctx.T("⬅ Menu"), "menu"));
            await Ctx.Ui.Show(m, Format(stats), buttons);
        }
        catch (Exception ex)
        {
           await Console.Error.WriteLineAsync($"Stats read failed: {ex}");
            await Ctx.Ui.Say(Ctx.T("⚠️ Statistics are temporarily unavailable."));
        }
    }

    private string Format(GlobalStats s)
    {
        var top = s.Top.Count == 0
            ? Ctx.T("—")
            : string.Join("\n", s.Top.Select((t, i) =>
                Ctx.F("{0}. {1} — {2} views · {3} pages", i + 1, Text.H(Text.Truncate(t.Title, 30)), Text.N(t.Views), Text.N(t.Pages))));

        return Ctx.F(
            "📊 <b>Detailed statistics</b> (admin) — {0:yyyy-MM-dd HH:mm} UTC\n\n👥 Users: {1} · active 24h {2} · 7d {3} · new 7d {4}\n📁 Albums: {5} (open {6}) · created 7d {7}\n🖼 Media: {8} (photos {9} · videos {10}) · added 7d {11}\n   avg per album {12:F1} · largest {13}\n🔑 Viewer grants: {14} · code joins {15} · wrong codes {16} · throttled {17}\n\n👀 Views (lifetime): {18} (ordered {19} · random {20})\n📄 Pages watched (lifetime): {21}\n➕ Created/added (lifetime): albums {22} · media {23}\n🗑 Deleted (lifetime): albums {24} · media {25}\n🔓 Albums opened for others: {26} times\n\n🏆 Most watched:\n{27}\n\n<i>Lifetime counters start at the upgrade date; earlier history is seeded only for created albums and added media.</i>",
            s.Now, Text.N(s.Users), Text.N(s.ActiveUsers24h), Text.N(s.ActiveUsers7d), Text.N(s.NewUsers7d),
            Text.N(s.Albums), Text.N(s.OpenAlbums), Text.N(s.AlbumsCreated7d), Text.N(s.Media), Text.N(s.Photos),
            Text.N(s.Videos), Text.N(s.MediaAdded7d), s.AvgMediaPerAlbum,
            Text.N(s.LargestAlbum), Text.N(s.ViewerGrants), Text.N(s.CodeJoins), Text.N(s.CodeFailures),
            Text.N(s.CodeThrottled), Text.N(s.ViewsTotal + s.RandomViewsTotal), Text.N(s.ViewsTotal),
            Text.N(s.RandomViewsTotal), Text.N(s.PagesTotal), Text.N(s.AlbumsCreatedTotal), Text.N(s.MediaAddedTotal),
            Text.N(s.AlbumsDeletedTotal), Text.N(s.MediaDeletedTotal), Text.N(s.AlbumsOpenedTimes), top);
    }
}
