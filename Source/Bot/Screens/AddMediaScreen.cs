using nAlbum.Bot.Ui;
using nAlbum.Data;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using TelegramBotBase.Sessions;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

public sealed class AddMediaScreen : Screen
{
    public AddMediaScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "add", "done" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.Adding };

    public override async Task OnText(MessageResult m, string text)
    {
        await Ctx.Ui.Say(Ctx.T(LocKey.AddOnlyMedia));
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
                bf.AddButtonRow(Ctx.T(LocKey.ButtonDone), $"done:{album.Id}");
                await Ctx.Ui.Say(Ctx.F(LocKey.AddPrompt, Text.H(album.Title)), bf);
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
                    summary = Ctx.F(LocKey.AddSaved, add.Added)
                              + (add.Skipped > 0 ? Ctx.F(LocKey.AddDuplicatesSkipped, add.Skipped) : "")
                              + (add.Removed > 0 ? Ctx.F(LocKey.AddDeletedFromChat, add.Removed) : "") + ".\n\n";
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
            if (Ctx.Session.Mode == Mode.Adding) await Ctx.Ui.Say(Ctx.T(LocKey.AddOnlyMedia));
            return;
        }

        if (Ctx.Session.Mode != Mode.Adding)
        {
            if (FirstOfGroup(msg)) await Ctx.Ui.Say(Ctx.T(LocKey.AddHintOpenAlbum), Ctx.Ui.MenuButtons());
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
            if (!await StillInChat(messageId))
            {
                add.Removed++;
                continue;
            }

            if (await Ctx.Media.AddMedia(albumId, media.Kind, media.FileId, media.UniqueId)) add.Added++;
            else add.Skipped++;
        }
        add.Pending.Clear();
    }

    private async Task<bool> StillInChat(int messageId)
    {
        try
        {
            await Ctx.Device.Dispatch(a => a.SetMessageReaction(
                Ctx.Device.DeviceId, messageId, [new ReactionTypeEmoji { Emoji = "👌" }]));
            return true;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            if (ex.Message.Contains("message to react not found", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("message_id_invalid", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("message not found", StringComparison.OrdinalIgnoreCase))
            {
                await Console.Error.WriteLineAsync($"Pending media {messageId} is gone: {ex.Message}");
                return false;
            }

            await Console.Error.WriteLineAsync($"Presence probe for {messageId} failed ({ex.ErrorCode}): {ex.Message}");
            return true;   // cannot tell; keep the media rather than lose it
        }
        catch (ApiRequestException ex)
        {
            await Console.Error.WriteLineAsync($"Presence probe for {messageId} failed ({ex.ErrorCode}): {ex.Message}");
            return true;   // cannot tell; keep the media rather than lose it
        }
        catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Presence probe for {messageId} failed: {ex.Message}");
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
            await Ctx.Device.Dispatch(a => a.SetMessageReaction(
                Ctx.Device.DeviceId, messageId, [new ReactionTypeEmoji { Emoji = emoji }]));
        }
        catch (ApiRequestException)
        {
            // reactions are a nicety; ignore failures
        }
    }
}
