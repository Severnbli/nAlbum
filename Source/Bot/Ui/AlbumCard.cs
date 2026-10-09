using System.Text;
using nAlbum.Data;
using TelegramBotBase.Form;

namespace nAlbum.Bot.Ui;

public sealed class AlbumCard
{
    public AlbumCard(BotContext ctx) { Ctx = ctx; }
    private BotContext Ctx { get; }

    public async Task<(string Text, ButtonForm Buttons)> Build(Album album)
    {
        var count = await Ctx.Media.MediaCount(album.Id);
        var watch = await Ctx.Stats.GetAlbumWatchAsync(album.Id);
        var isOwner = album.OwnerId == Ctx.UserId;

        var sb = new StringBuilder();
        sb.Append($"📁 <b>{Text.H(album.Title)}</b>");
        if (watch != null) sb.Append('\n').Append(Ctx.F("👀 Views: {0} · 📄 Pages: {1}", Text.N(watch.Views), Text.N(watch.Pages)));
        sb.Append('\n').Append(Ctx.F("🖼 Media: {0}", count));
        if (isOwner && album.NextNumber > 1) sb.Append(Ctx.F(" · last number #{0}", album.NextNumber - 1));

        var bf = new ButtonForm();
        if (!isOwner)
        {
            bf.AddButtonRow(new ButtonBase(Ctx.T("👀 View"), $"view:{album.Id}:0"), new ButtonBase(Ctx.T("🎲 Random"), $"rnd:{album.Id}"));
            bf.AddButtonRow(new ButtonBase(Ctx.T("🚪 Leave"), $"leave:{album.Id}"), new ButtonBase(Ctx.T("⬅ Back"), "shared:0"));
            return (sb.ToString(), bf);
        }

        if (album.IsOpen)
        {
            sb.Append(Ctx.F("\n🔓 Open for others\nCode: <code>{0}</code>", album.AccessCode));
            sb.Append(Ctx.F("\nLink: https://t.me/{0}?start={1}", BotInfo.Username, album.AccessCode));
        }
        else
        {
            sb.Append(Ctx.T("\n🔒 Closed – only you can see it"));
        }

        bf.AddButtonRow(new ButtonBase(Ctx.T("👀 View"), $"view:{album.Id}:0"), new ButtonBase(Ctx.T("🎲 Random"), $"rnd:{album.Id}"));
        bf.AddButtonRow(new ButtonBase(Ctx.T("➕ Add media"), $"add:{album.Id}"), new ButtonBase(Ctx.T("🗑 Remove media"), $"rmlist:{album.Id}:0"));
        bf.AddButtonRow(Ctx.T("✏️ Rename"), $"ren:{album.Id}");
        bf.AddButtonRow(Ctx.T(album.IsOpen ? "🔒 Close album" : "🔓 Open for others"), $"toggle:{album.Id}");
        bf.AddButtonRow(Ctx.T("❌ Delete album"), $"del:{album.Id}");
        bf.AddButtonRow(Ctx.T("⬅ My albums"), "mine:0");
        return (sb.ToString(), bf);
    }
}
