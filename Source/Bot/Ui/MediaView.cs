using System.Net;
using nAlbum.Data;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using TelegramBotBase.Form;
using TelegramBotBase.Sessions;

namespace nAlbum.Bot.Ui;

public sealed class MediaView
{
    public MediaView(BotContext ctx) { Ctx = ctx; }
    private BotContext Ctx { get; }

    private static string CaptionAt(IReadOnlyList<string> captions, int i) =>
        captions != null && i < captions.Count ? captions[i] : null;

    /// <summary>
    /// Telegram shows an album caption under the grid only when exactly ONE item has a caption,
    /// so all numbers (in display order) go into the caption of the first item.
    /// </summary>
    public static List<string> NumberCaptions(IReadOnlyList<int> numbers, Func<string, string> translate)
    {
        if (numbers.Count == 0) return null;
        var captions = new List<string>(new string[numbers.Count]); // all null
        captions[0] = numbers.Count == 1
            ? $"#{numbers[0]}"
            : translate("Numbers in order: ") + string.Join("  ", numbers.Select(n => $"#{n}"));
        return captions;
    }

    public async Task<int> SendOne(MediaItem item, ButtonForm bf, string caption = null)
    {
        var msg = item.Kind == MediaKind.Photo
            ? await Ctx.Device.SendPhoto(InputFile.FromFileId(item.FileId), caption, buttons: bf)
            : await Ctx.Device.SendVideo(InputFile.FromFileId(item.FileId), caption, buttons: bf);
        return msg?.MessageId ?? 0;
    }

    private static InputMedia ToInput(MediaItem i, string caption) =>
        i.Kind == MediaKind.Photo
            ? new InputMediaPhoto(InputFile.FromFileId(i.FileId)) { Caption = caption }
            : new InputMediaVideo(InputFile.FromFileId(i.FileId)) { Caption = caption };

    public async Task<(List<int> Ids, int Failed)> SendBatch(List<MediaItem> items,
        IReadOnlyList<string> captions = null)
    {
        var ids = new List<int>();
        if (items.Count == 0) return (ids, 0);

        if (items.Count == 1)
        {
            try
            {
                var id = await SendOne(items[0], null, CaptionAt(captions, 0));
                if (id != 0) ids.Add(id);
                return (ids, 0);
            }
            catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
            {
                await Console.Error.WriteLineAsync($"Media {items[0].Id} could not be sent: {ex.Message}");
                return (ids, 1);
            }
        }

        var group = items
            .Select((item, idx) => (IAlbumInputMedia)ToInput(item, CaptionAt(captions, idx)))
            .ToList();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var sent = await Ctx.Device.Dispatch(a => a.SendMediaGroup(Ctx.Device.DeviceId, group));
                ids.AddRange(sent.Select(s => s.MessageId));
                return (ids, 0);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                var wait = Math.Min(ex.Parameters?.RetryAfter ?? 3, 30) + 1;
                await Console.Error.WriteLineAsync($"Flood limit on group of {items.Count}, resending in {wait}s");
                await Task.Delay(TimeSpan.FromSeconds(wait));
            }
            catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
            {
                await Console.Error.WriteLineAsync(
                    $"Media group of {items.Count} rejected: {ex.Message}. Splitting to isolate the bad item.");
                var mid = items.Count / 2;
                var left = await SendBatch(items.Take(mid).ToList(), captions?.Take(mid).ToList());
                var right = await SendBatch(items.Skip(mid).ToList(), captions?.Skip(mid).ToList());
                left.Ids.AddRange(right.Ids);
                return (left.Ids, left.Failed + right.Failed);
            }
        }

        await Console.Error.WriteLineAsync($"Gave up on group of {items.Count} after repeated flood limits");
        return (ids, items.Count);
    }

    public async Task<bool> TryEditGroup(List<MediaItem> items, IReadOnlyList<string> captions)
    {
        var v = Ctx.Session.View;
        for (var i = 0; i < items.Count; i++)
        {
            if (!await EditMedia(v.Ids[i], ToInput(items[i], CaptionAt(captions, i)))) return false;
        }

        return true;
    }

    public async Task<bool> EditMedia(int messageId, InputMedia media)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await Ctx.Device.Dispatch(a => a.EditMessageMedia(Ctx.Device.DeviceId, messageId, media));
                return true;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(ex.Parameters?.RetryAfter ?? 3, 30) + 1));
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
            {
                return true; // same media is already there
            }
            catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
            {
                await Console.Error.WriteLineAsync($"Edit of message {messageId} failed: {ex.Message}");
                return false; // caller falls back to rebuilding the view
            }
        }

        return false;
    }

    /// <summary>
    /// Shows one page. clickedMessageId = the message the user clicked (card or navigation message),
    /// or the navigation message id when a typed text triggered the refresh. Edits in place when possible.
    /// </summary>
    public async Task ShowViewPage(int clickedMessageId, List<MediaItem> items, Func<int, string> textFor,
        ButtonForm nav, IReadOnlyList<string> captions = null)
    {
        var v = Ctx.Session.View;
        var clickedNav = v.NavId != 0 && clickedMessageId == v.NavId;

        if (clickedNav && v.Editable && v.Ids.Count == items.Count && await TryEditGroup(items, captions))
        {
            await Ctx.Ui.EditNav(textFor(0), nav);
            return;
        }

        await ClearView();
        if (!clickedNav && clickedMessageId != 0) await Ctx.Ui.TryDelete(clickedMessageId); // the card / a stale nav

        var (ids, failed) = await SendBatch(items, captions);
        v.Ids.AddRange(ids);
        v.Editable = failed == 0 && ids.Count == items.Count;
        var navMsg = await Ctx.Ui.Say(textFor(failed), nav);
        v.NavId = navMsg?.MessageId ?? 0;
    }

    public async Task ClearView(int keepMessageId = 0)
    {
        var v = Ctx.Session.View;
        var all = new List<int>(v.Ids);
        if (v.NavId != 0 && v.NavId != keepMessageId) all.Add(v.NavId);
        v.Ids.Clear();
        v.NavId = 0;
        v.Editable = false;
        v.LastPageKey = null;
        if (all.Count == 0) return;

        try
        {
            await Ctx.Device.Dispatch(a => a.DeleteMessages(Ctx.Device.DeviceId, all));
        }
        catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Could not delete old view: {ex.Message}");
        }
    }
}
