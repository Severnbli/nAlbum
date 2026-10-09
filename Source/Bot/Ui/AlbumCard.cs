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
        var isOwner = album.OwnerId == Ctx.UserId;

        var sb = new StringBuilder();
        sb.Append($"📁 <b>{Text.H(album.Title)}</b>\n🖼 Media: {count}");

        var bf = new ButtonForm();
        if (!isOwner)
        {
            bf.AddButtonRow(new ButtonBase("👀 View", $"view:{album.Id}:0"), new ButtonBase("🎲 Random", $"rnd:{album.Id}"));
            bf.AddButtonRow(new ButtonBase("🚪 Leave", $"leave:{album.Id}"), new ButtonBase("⬅ Back", "shared:0"));
            return (sb.ToString(), bf);
        }

        if (album.IsOpen)
        {
            sb.Append($"\n🔓 Open for others\nCode: <code>{album.AccessCode}</code>");
            sb.Append($"\nLink: https://t.me/{BotInfo.Username}?start={album.AccessCode}");
        }
        else
        {
            sb.Append("\n🔒 Closed – only you can see it");
        }

        bf.AddButtonRow(new ButtonBase("👀 View", $"view:{album.Id}:0"), new ButtonBase("🎲 Random", $"rnd:{album.Id}"));
        bf.AddButtonRow(new ButtonBase("➕ Add media", $"add:{album.Id}"), new ButtonBase("🗑 Remove media", $"rmlist:{album.Id}:0"));
        bf.AddButtonRow("✏️ Rename", $"ren:{album.Id}");
        bf.AddButtonRow(album.IsOpen ? "🔒 Close album" : "🔓 Open for others", $"toggle:{album.Id}");
        bf.AddButtonRow("❌ Delete album", $"del:{album.Id}");
        bf.AddButtonRow("⬅ My albums", "mine:0");
        return (sb.ToString(), bf);
    }
}
