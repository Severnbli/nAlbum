using System.Globalization;
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
            await m.ConfirmAction("Not allowed.", true);
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
            buttons.AddButtonRow(new ButtonBase("🔄 Refresh", "stats"), new ButtonBase("⬅ Menu", "menu"));
            await Ctx.Ui.Show(m, Format(stats), buttons);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stats read failed: {ex}");
            await Ctx.Ui.Say("⚠️ Statistics are temporarily unavailable.");
        }
    }

    private static string Format(GlobalStats s)
    {
        var top = s.Top.Count == 0
            ? "—"
            : string.Join("\n", s.Top.Select((t, i) =>
                $"{i + 1}. {Text.H(Text.Truncate(t.Title, 30))} — {Text.N(t.Views)} views · {Text.N(t.Pages)} pages"));

        return
            $"📊 <b>Detailed statistics</b> (admin) — {s.Now:yyyy-MM-dd HH:mm} UTC\n\n" +
            $"👥 Users: {Text.N(s.Users)} · active 24h {Text.N(s.ActiveUsers24h)} · 7d {Text.N(s.ActiveUsers7d)} · new 7d {Text.N(s.NewUsers7d)}\n" +
            $"📁 Albums: {Text.N(s.Albums)} (open {Text.N(s.OpenAlbums)}) · created 7d {Text.N(s.AlbumsCreated7d)}\n" +
            $"🖼 Media: {Text.N(s.Media)} (photos {Text.N(s.Photos)} · videos {Text.N(s.Videos)}) · added 7d {Text.N(s.MediaAdded7d)}\n" +
            $"   avg per album {s.AvgMediaPerAlbum.ToString("F1", CultureInfo.InvariantCulture)} · largest {Text.N(s.LargestAlbum)}\n" +
            $"🔑 Viewer grants: {Text.N(s.ViewerGrants)} · code joins {Text.N(s.CodeJoins)} · wrong codes {Text.N(s.CodeFailures)} · throttled {Text.N(s.CodeThrottled)}\n\n" +
            $"👀 Views (lifetime): {Text.N(s.ViewsTotal + s.RandomViewsTotal)} (ordered {Text.N(s.ViewsTotal)} · random {Text.N(s.RandomViewsTotal)})\n" +
            $"📄 Pages watched (lifetime): {Text.N(s.PagesTotal)}\n" +
            $"➕ Created/added (lifetime): albums {Text.N(s.AlbumsCreatedTotal)} · media {Text.N(s.MediaAddedTotal)}\n" +
            $"🗑 Deleted (lifetime): albums {Text.N(s.AlbumsDeletedTotal)} · media {Text.N(s.MediaDeletedTotal)}\n" +
            $"🔓 Albums opened for others: {Text.N(s.AlbumsOpenedTimes)} times\n\n" +
            $"🏆 Most watched:\n{top}\n\n" +
            "<i>Lifetime counters start at the upgrade date; earlier history is seeded only for created albums and added media.</i>";
    }
}
