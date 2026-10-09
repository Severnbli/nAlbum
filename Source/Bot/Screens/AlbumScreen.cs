using nAlbum.Bot.Ui;
using nAlbum.Services;
using TelegramBotBase.Base;
using TelegramBotBase.Form;

namespace nAlbum.Bot.Screens;

public sealed class AlbumScreen : Screen
{
    public AlbumScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "al", "new", "toggle", "ren", "del", "delok" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.NewTitle, Mode.Rename };
    public override IReadOnlyCollection<string> Commands => new[] { "/new" };

    public override Task OnCommand(MessageResult m, string command, IReadOnlyList<string> args) => StartNew();

    public override async Task OnText(MessageResult m, string text)
    {
        switch (Ctx.Session.Mode)
        {
            case Mode.NewTitle:
            {
                var title = Text.CleanTitle(text);
                if (title.Length == 0)
                {
                    await Ctx.Ui.Say(Ctx.T("Title can't be empty. Try again or /cancel."));
                    return;
                }

                Ctx.Session.Mode = Mode.Idle;
                var album = await Ctx.Albums.CreateAlbum(Ctx.UserId, title);
                await Ctx.Stats.IncrementAsync(StatKeys.AlbumsCreated);
                var (card, bf) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Say(Ctx.T("✅ Album created.\n\n") + card, bf);
                return;
            }

            case Mode.Rename:
            {
                var title = Text.CleanTitle(text);
                if (title.Length == 0)
                {
                    await Ctx.Ui.Say(Ctx.T("Title can't be empty. Try again or /cancel."));
                    return;
                }

                Ctx.Session.Mode = Mode.Idle;
                var album = await Ctx.Albums.GetAlbum(Ctx.Session.AlbumId);
                if (album == null || album.OwnerId != Ctx.UserId) return;
                await Ctx.Albums.Rename(album.Id, title);
                album.Title = title;
                var (card, bf) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Say(card, bf);
                return;
            }
        }
    }

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        switch (p[0])
        {
            case "new":
                await m.ConfirmAction();
                await StartNew();
                break;

            case "al":
            {
                Ctx.Session.Mode = Mode.Idle;
                var album = await Ctx.Ui.ViewableAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                await Ctx.View.ClearView(m.MessageId);
                var (card, bf) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Show(m, card, bf);
                break;
            }

            case "toggle":
                await OnToggle(m, p);
                break;

            case "ren":
            {
                var album = await Ctx.Ui.OwnedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                Ctx.Session.Mode = Mode.Rename;
                Ctx.Session.AlbumId = album.Id;
                await Ctx.Ui.Say(Ctx.T("Send the new title (or /cancel)."));
                break;
            }

            case "del":
            {
                var album = await Ctx.Ui.OwnedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                var bf = new ButtonForm();
                bf.AddButtonRow(new ButtonBase(Ctx.T("Yes, delete"), $"delok:{album.Id}"), new ButtonBase(Ctx.T("Cancel"), $"al:{album.Id}"));
                await Ctx.Ui.Show(m, Ctx.F("Delete <b>{0}</b> and all its media references? This can't be undone.", Text.H(album.Title)), bf);
                break;
            }

            case "delok":
            {
                var album = await Ctx.Ui.OwnedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction(Ctx.T("Album deleted."));
                var media = await Ctx.Albums.DeleteAlbum(album.Id);
                await Ctx.Stats.IncrementAsync(StatKeys.AlbumsDeleted);
                await Ctx.Stats.IncrementAsync(StatKeys.MediaDeleted, media);
                await Ctx.Ui.ShowList(m, "mine", 0);
                break;
            }
        }
    }

    private async Task StartNew()
    {
        Ctx.Session.Mode = Mode.NewTitle;
        await Ctx.Ui.Say(Ctx.T("Send the album title (or /cancel)."));
    }

    private async Task OnToggle(MessageResult m, string[] p)
    {
        var album = await Ctx.Ui.OwnedAlbum(m, p);
        if (album == null) return;

        string code = null;
        if (album.IsOpen)
        {
            await Ctx.Albums.CloseAlbum(album.Id);
            await Ctx.Stats.IncrementAsync(StatKeys.AlbumsClosed);
            await m.ConfirmAction(Ctx.T("Album closed. Everyone else lost access."), true);
        }
        else
        {
            code = await Ctx.Albums.OpenAlbum(album.Id);
            await Ctx.Stats.IncrementAsync(StatKeys.AlbumsOpened);
            await m.ConfirmAction(Ctx.T("Album opened."));
        }

        album = await Ctx.Albums.GetAlbum(album.Id);
        var (card, bf) = await Ctx.Cards.Build(album);
        await Ctx.Ui.Show(m, card, bf);

        if (code != null)
        {
            await Ctx.Ui.Say(Ctx.F("🔓 <b>{0}</b> is open.\n\nAccess code: <code>{1}</code>\nLink: https://t.me/{2}?start={1}\n\nAnyone with the code can watch the album (but not edit it). Closing the album invalidates the code and removes all viewers.",
                Text.H(album.Title), code, BotInfo.Username));
        }
    }
}
