using nAlbum.Bot.Ui;
using nAlbum.Data;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
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
        if (Ctx.Session.Mode == Mode.Adding && text == Ctx.T(LocKey.ButtonDone))
        {
            var album = await Ctx.Albums.GetAlbum(Ctx.Session.AlbumId);
            if (album == null || album.OwnerId != Ctx.UserId)
            {
                Ctx.Session.Mode = Mode.Idle;
                return;
            }

            await FinishAdding(m, album);
            return;
        }

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
                await Ctx.Ui.TryDelete(m.MessageId);
                var add = Ctx.Session.Add;
                await add.Gate.WaitAsync();
                try
                {
                    Ctx.Session.Mode = Mode.Adding;
                    Ctx.Session.AlbumId = album.Id;
                    add.Reset();
                    add.PromptText = Ctx.F(LocKey.AddPrompt, Text.H(album.Title));
                    await SendAddPrompt(album.Id);
                }
                finally
                {
                    add.Gate.Release();
                }
                break;
            }

            case "done":
            {
                var album = await Ctx.Ui.OwnedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                await FinishAdding(m, album);
                break;
            }
        }
    }

    public override async Task OnMedia(DataResult data)
    {
        var msg = data.Message;
        if (!TryExtract(msg, out var pending))
        {
            await TrackUserMessage(msg, IsUnsupportedMedia(msg));
            return;
        }

        var add = Ctx.Session.Add;
        await add.Gate.WaitAsync();
        try
        {
            if (Ctx.Session.Mode != Mode.Adding)
            {
                if (FirstOfGroup(msg)) await Ctx.Ui.Say(Ctx.T(LocKey.AddHintOpenAlbum), Ctx.Ui.MenuButtons());
                return;
            }

            add.Pending[msg.MessageId] = pending;

            // Telegram applies a reaction on any item of a media group to the first message of the group,
            // so react once per group.
            if (msg.MediaGroupId == null || FirstOfGroup(msg)) await React(msg.MessageId, "❤️");
        }
        finally
        {
            add.Gate.Release();
        }
    }

    public async Task OnIncomingMessage(Message msg)
    {
        if (msg == null) return;
        await TrackUserMessage(msg, IsUnsupportedMedia(msg));
    }

    private async Task TrackUserMessage(Message msg, bool unsupported)
    {
        var add = Ctx.Session.Add;
        await add.Gate.WaitAsync();
        try
        {
            if (Ctx.Session.Mode != Mode.Adding) return;
            add.UserMessages.TryAdd(msg.MessageId, 0);
            if (unsupported && add.UnsupportedHandled.TryAdd(msg.MessageId, 0))
                await React(msg.MessageId, "👎");
        }
        finally
        {
            add.Gate.Release();
        }
    }

    private static bool IsUnsupportedMedia(Message msg) =>
        msg.Animation != null || msg.Audio != null || msg.Document != null || msg.Sticker != null
        || msg.VideoNote != null || msg.Voice != null;

    private async Task SendAddPrompt(long albumId)
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(Ctx.T(LocKey.ButtonDone), $"done:{albumId}");
        var prompt = await Ctx.Ui.Say(Ctx.Session.Add.PromptText, bf);
        Ctx.Session.Add.PromptMessageId = prompt?.MessageId ?? 0;

        var keyboard = new ReplyKeyboardMarkup(new KeyboardButton(Ctx.T(LocKey.ButtonDone)))
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };
        var keyboardMessage = await Ctx.Device.Dispatch(a => a.SendMessage(Ctx.Device.DeviceId,
            Ctx.T(LocKey.AddKeyboardHint), parseMode: ParseMode.Html, replyMarkup: keyboard));
        Ctx.Session.Add.KeyboardMessageId = keyboardMessage?.MessageId ?? 0;
    }

    private async Task FinishAdding(MessageResult m, Album album)
    {
        var add = Ctx.Session.Add;
        var shouldCommit = false;
        await add.Gate.WaitAsync();
        try
        {
            shouldCommit = Ctx.Session.Mode == Mode.Adding && Ctx.Session.AlbumId == album.Id;
            if (shouldCommit)
            {
                Ctx.Session.Mode = Mode.Idle;
                if (add.PromptMessageId != 0) await Ctx.Ui.TryDelete(add.PromptMessageId);
                if (add.KeyboardMessageId != 0) await Ctx.Ui.TryDelete(add.KeyboardMessageId);
                add.PromptMessageId = add.KeyboardMessageId = 0;
            }
        }
        finally
        {
            add.Gate.Release();
        }

        if (shouldCommit)
        {
            await Commit(album.Id);
            foreach (var messageId in add.UserMessages.Keys)
                await Ctx.Ui.TryDelete(messageId);
            add.UserMessages.Clear();
        }

        var summary = shouldCommit
            ? Ctx.F(LocKey.AddSaved, add.Added)
              + (add.Skipped > 0 ? Ctx.F(LocKey.AddDuplicatesSkipped, add.Skipped) : "")
              + (add.Removed > 0 ? Ctx.F(LocKey.AddDeletedFromChat, add.Removed) : "") + ".\n\n"
            : "";
        var cardAlbum = shouldCommit ? await Ctx.Albums.GetAlbum(album.Id) : album;
        if (cardAlbum == null)
        {
            if (summary.Length > 0) await Ctx.Ui.Say(summary);
            return;
        }

        var (card, bf) = await Ctx.Cards.Build(cardAlbum);
        await Ctx.Ui.Say(summary + card, bf);
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

    /// <summary>Saves pending media that can be removed from the chat.</summary>
    private async Task Commit(long albumId)
    {
        var add = Ctx.Session.Add;
        foreach (var (messageId, media) in add.Pending.OrderBy(kv => kv.Key))
        {
            if (!await DeleteIfPresent(messageId))
            {
                add.Removed++;
                continue;
            }

            if (await Ctx.Media.AddMedia(albumId, media.Kind, media.FileId, media.UniqueId)) add.Added++;
            else add.Skipped++;
        }
        add.Pending.Clear();
    }

    private async Task<bool> DeleteIfPresent(int messageId)
    {
        try
        {
            await Ctx.Device.Dispatch(a => a.DeleteMessage(Ctx.Device.DeviceId, messageId));
            return true;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            if (ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("message_id_invalid", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("message not found", StringComparison.OrdinalIgnoreCase))
            {
                await Console.Error.WriteLineAsync($"Pending media {messageId} is already gone: {ex.Message}");
                return false;
            }

            await Console.Error.WriteLineAsync($"Deletion check for {messageId} failed ({ex.ErrorCode}): {ex.Message}");
            return true;   // cannot tell; keep the media rather than lose it
        }
        catch (ApiRequestException ex)
        {
            await Console.Error.WriteLineAsync($"Deletion check for {messageId} failed ({ex.ErrorCode}): {ex.Message}");
            return true;   // cannot tell; keep the media rather than lose it
        }
        catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Deletion check for {messageId} failed: {ex.Message}");
            return true;   // cannot tell; keep the media rather than lose it
        }
    }

    private bool FirstOfGroup(Message msg)
    {
        if (msg.MediaGroupId == null) return true;
        return Ctx.Session.Add.SeenMediaGroups.Add(msg.MediaGroupId);
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
