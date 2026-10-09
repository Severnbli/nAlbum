using nAlbum.Bot.Ui;
using nAlbum.Data;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotBase.Base;
using TelegramBotBase.Form;

namespace nAlbum.Bot.Screens;

public sealed class AddMediaScreen : Screen
{
    public AddMediaScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "add", "done" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.Adding };

    public override async Task OnText(MessageResult m, string text)
    {
        await Ctx.Ui.Say(Ctx.T("Only photos and videos can be added. Press Done when finished."));
    }

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        switch (p[0])
        {
            case "add":
            {
                var album = await Ctx.Ui.OwnedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                Ctx.Session.Mode = Mode.Adding;
                Ctx.Session.AlbumId = album.Id;
                Ctx.Session.Add.Added = Ctx.Session.Add.Skipped = 0;
                var bf = new ButtonForm();
                bf.AddButtonRow(Ctx.T("✅ Done"), $"done:{album.Id}");
                await Ctx.Ui.Say(Ctx.F("📥 Adding to <b>{0}</b>.\nSend or forward photos and videos – as many as you like. Duplicates are skipped automatically.\nPress <b>Done</b> when finished.", Text.H(album.Title)), bf);
                break;
            }

            case "done":
            {
                var album = await Ctx.Ui.OwnedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                await Ctx.View.ClearView(m.MessageId);
                var add = Ctx.Session.Add;
                var summary = Ctx.Session.Mode == Mode.Adding && Ctx.Session.AlbumId == album.Id
                    ? Ctx.F("✅ Saved: {0} added", add.Added) + (add.Skipped > 0 ? Ctx.F(", {0} duplicates skipped", add.Skipped) : "") + ".\n\n"
                    : "";
                Ctx.Session.Mode = Mode.Idle;
                var (card, bf) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Show(m, summary + card, bf);
                break;
            }
        }
    }

    public override async Task OnMedia(DataResult data)
    {
        var msg = data.Message;
        MediaKind kind;
        string fileId, uniqueId;

        if (data.Type == MessageType.Photo && msg.Photo is { Length: > 0 })
        {
            var photo = msg.Photo[^1]; // largest size
            (kind, fileId, uniqueId) = (MediaKind.Photo, photo.FileId, photo.FileUniqueId);
        }
        else if (data.Type == MessageType.Video && msg.Video != null)
        {
            (kind, fileId, uniqueId) = (MediaKind.Video, msg.Video.FileId, msg.Video.FileUniqueId);
        }
        else
        {
            if (Ctx.Session.Mode == Mode.Adding) await Ctx.Ui.Say(Ctx.T("Only photos and videos can be added. Press Done when finished."));
            return;
        }

        if (Ctx.Session.Mode != Mode.Adding)
        {
            if (FirstOfGroup(msg)) await Ctx.Ui.Say(Ctx.T("To add media, open one of your albums and press ➕ Add media."), Ctx.Ui.MenuButtons());
            return;
        }

        var album = await Ctx.Albums.GetAlbum(Ctx.Session.AlbumId);
        if (album == null || album.OwnerId != Ctx.UserId)
        {
            Ctx.Session.Mode = Mode.Idle;
            return;
        }

        var added = await Ctx.Media.AddMedia(album.Id, kind, fileId, uniqueId);
        if (added) Ctx.Session.Add.Added++;
        else Ctx.Session.Add.Skipped++;

        // Telegram applies a reaction on any item of a media group to the first message of the group,
        // so react once per group.
        if (msg.MediaGroupId == null) await React(msg.MessageId, added ? "👍" : "🤔");
        else if (FirstOfGroup(msg)) await React(msg.MessageId, "👍");
    }

    private bool FirstOfGroup(Message msg)
    {
        if (msg.MediaGroupId == null) return true;
        if (msg.MediaGroupId == Ctx.Session.Add.LastGroupId) return false;
        Ctx.Session.Add.LastGroupId = msg.MediaGroupId;
        return true;
    }

    private async Task React(int messageId, string emoji)
    {
        try
        {
            await Ctx.Device.Api(a => a.SetMessageReaction(
                Ctx.Device.DeviceId, messageId, [new ReactionTypeEmoji { Emoji = emoji }]));
        }
        catch (ApiRequestException)
        {
            // reactions are a nicety; ignore failures
        }
    }
}
