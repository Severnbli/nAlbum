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
                Ctx.Session.Add.Reset();
                var bf = new ButtonForm();
                bf.AddButtonRow(Ctx.T("âœ… Done"), $"done:{album.Id}");
                await Ctx.Ui.Say(Ctx.F("ðŸ“¥ Adding to <b>{0}</b>.\nSend or forward photos and videos â€“ as many as you like. Nothing is saved until you press <b>Done</b>; duplicates are skipped automatically.\nMedia you edit or delete before then is tracked.", Text.H(album.Title)), bf);
                break;
            }

            case "done":
            {
                var album = await Ctx.Ui.OwnedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                await Ctx.View.ClearView(m.MessageId);
                var add = Ctx.Session.Add;
                var summary = "";
                if (Ctx.Session.Mode == Mode.Adding && Ctx.Session.AlbumId == album.Id)
                {
                    await Commit(album.Id);
                    summary = Ctx.F("✅ Saved: {0} added", add.Added)
                              + (add.Skipped > 0 ? Ctx.F(", {0} duplicates skipped", add.Skipped) : "")
                              + (add.Removed > 0 ? Ctx.F(", {0} deleted from the chat", add.Removed) : "") + ".\n\n";
                }
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
        if (!TryExtract(msg, out var pending))
        {
            if (Ctx.Session.Mode == Mode.Adding) await Ctx.Ui.Say(Ctx.T("Only photos and videos can be added. Press Done when finished."));
            return;
        }

        if (Ctx.Session.Mode != Mode.Adding)
        {
            if (FirstOfGroup(msg)) await Ctx.Ui.Say(Ctx.T("To add media, open one of your albums and press ➕ Add media."), Ctx.Ui.MenuButtons());
            return;
        }

        Ctx.Session.Add.Pending[msg.MessageId] = pending;

        // Telegram applies a reaction on any item of a media group to the first message of the group,
        // so react once per group.
        if (msg.MediaGroupId == null || FirstOfGroup(msg)) await React(msg.MessageId, "👀");
    }

    public async Task OnEdited(MessageResult m)
    {
        var msg = m.UpdateData.EditedMessage;
        if (msg == null || Ctx.Session.Mode != Mode.Adding) return;
        var pending = Ctx.Session.Add.Pending;
        if (!pending.ContainsKey(msg.MessageId)) return;

        if (TryExtract(msg, out var updated)) pending[msg.MessageId] = updated;   // media replaced (or only caption changed)
        else pending.TryRemove(msg.MessageId, out _);
    }

    private static bool TryExtract(Message msg, out PendingMedia media)
    {
        var inGroup = msg.MediaGroupId != null;
        media = null;
        if (msg.Photo is { Length: > 0 })
        {
            var photo = msg.Photo[^1]; // largest size
            media = new PendingMedia(MediaKind.Photo, photo.FileId, photo.FileUniqueId, inGroup);
        }
        else if (msg.Video != null)
        {
            media = new PendingMedia(MediaKind.Video, msg.Video.FileId, msg.Video.FileUniqueId, inGroup);
        }
        return media != null;
    }

    /// <summary>Saves pending media still present in the chat. Bots get no delete events, so presence is probed.</summary>
    private async Task Commit(long albumId)
    {
        var add = Ctx.Session.Add;
        foreach (var (messageId, media) in add.Pending.OrderBy(kv => kv.Key))
        {
            if (!await StillInChat(messageId, media.InGroup))
            {
                add.Removed++;
                continue;
            }

            if (await Ctx.Media.AddMedia(albumId, media.Kind, media.FileId, media.UniqueId)) add.Added++;
            else add.Skipped++;
        }
        add.Pending.Clear();
    }

    private async Task<bool> StillInChat(int messageId, bool inGroup)
    {
        try
        {
            if (inGroup)
            {
                // reactions on group items hit the first message, so probe by copying and removing the copy
                var copy = await Ctx.Device.Api(a => a.CopyMessage(Ctx.Device.DeviceId, Ctx.Device.DeviceId, messageId));
                await Ctx.Device.Api(a => a.DeleteMessage(Ctx.Device.DeviceId, copy.Id));
            }
            else
            {
                await Ctx.Device.Api(a => a.SetMessageReaction(
                    Ctx.Device.DeviceId, messageId, [new ReactionTypeEmoji { Emoji = "👍" }]));
            }
            return true;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400
            && (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("MESSAGE_ID_INVALID", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        catch (ApiRequestException)
        {
            return true;   // cannot tell; keep the media rather than lose it
        }
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

