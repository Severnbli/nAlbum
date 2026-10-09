using System.Text;
using nAlbum.Data;
using TelegramBotBase.Form;
using nAlbum.Localization;

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
        if (watch != null) sb.Append('\n').Append(Ctx.F(LocKey.CardViewStats, Text.N(watch.Views), Text.N(watch.Pages)));
        sb.Append('\n').Append(Ctx.F(LocKey.CardMedia, count));
        if (isOwner && album.NextNumber > 1) sb.Append(Ctx.F(LocKey.CardLastNumber, album.NextNumber - 1));

        var bf = new ButtonForm();
        if (!isOwner)
        {
            bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonView), $"view:{album.Id}:0"), new ButtonBase(Ctx.T(LocKey.ButtonRandom), $"rnd:{album.Id}"));
            bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonLeave), $"leave:{album.Id}"), new ButtonBase(Ctx.T(LocKey.ButtonBack), "shared:0"));
            return (sb.ToString(), bf);
        }

        if (album.IsOpen)
        {
            sb.Append(Ctx.F(LocKey.CardOpen, album.AccessCode));
            sb.Append(Ctx.F(LocKey.CardLink, BotInfo.Username, album.AccessCode));
        }
        else
        {
            sb.Append(Ctx.T(LocKey.CardClosed));
        }

        bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonView), $"view:{album.Id}:0"), new ButtonBase(Ctx.T(LocKey.ButtonRandom), $"rnd:{album.Id}"));
        bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonAddMedia), $"add:{album.Id}"), new ButtonBase(Ctx.T(LocKey.ButtonRemoveMedia), $"rmlist:{album.Id}:0"));
        bf.AddButtonRow(Ctx.T(LocKey.ButtonRename), $"ren:{album.Id}");
        bf.AddButtonRow(Ctx.T(album.IsOpen ? LocKey.ButtonCloseAlbum : LocKey.ButtonOpenAlbum), $"toggle:{album.Id}");
        bf.AddButtonRow(Ctx.T(LocKey.ButtonDeleteAlbum), $"del:{album.Id}");
        bf.AddButtonRow(Ctx.T(LocKey.ButtonMyAlbums), "mine:0");
        return (sb.ToString(), bf);
    }
}
