using System.Net;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using TelegramBotBase.Sessions;

namespace nAlbum.Source;

/// <summary>
/// The only form of the bot (one instance per user session). All rendering is done explicitly in
/// Load / SentData / Action, so Render() is intentionally empty.
///
/// Callback data:
///   menu | new | mine:&lt;page&gt; | shared:&lt;page&gt; | al:&lt;album&gt;
///   view:&lt;album&gt;:&lt;offset&gt; | vp:&lt;album&gt;:&lt;offset&gt;:&lt;flags&gt;
///   rnd:&lt;album&gt; | rp:&lt;album&gt;:&lt;seed&gt;:&lt;offset&gt;:&lt;flags&gt;      flags: 1 = numbers, 2 = delete mode (3 = both)
///   add:&lt;album&gt; | done:&lt;album&gt;                              done: add-media only
///   rmlist:&lt;album&gt;:&lt;ignored&gt;                                opens the "type numbers" prompt
///   rmt:&lt;mediaId&gt;:&lt;offset&gt; | rmp:&lt;album&gt;:&lt;offset&gt; | rmy:&lt;album&gt; | rmc:&lt;album&gt;   remove preview: toggle / page / confirm / cancel
///   toggle:&lt;album&gt; | ren:&lt;album&gt; | del:&lt;album&gt; | delok:&lt;album&gt;
///   leave:&lt;album&gt;
/// Every owner action re-checks ownership on the server side.
/// </summary>
public class AlbumsForm : FormBase
{
    private const int ListPage = 8;    // albums per page in lists
    private const int ViewPage = 10;   // media per page when watching (= one Telegram media group)

    private const string Welcome =
        "👋 <b>Albums bot</b>\n\n" +
        "Create albums of photos and videos and share them with a secret code.\n" +
        "Got a code from a friend? Just send it to me.";

    private enum Mode { Idle, NewTitle, Rename, Adding, Removing, RemovePrompt }

    private readonly AlbumService _albums;

    private Mode _mode = Mode.Idle;
    private long _albumId;          // album being renamed / filled
    private int _added, _skipped;   // counters of the current "add media" session
    private string _lastGroupId;    // last media-group id we already reacted to
    private readonly List<int> _viewIds = new(); // message ids of the album currently shown (in order)
    private int _viewNavId;                      // id of its navigation message
    private bool _viewEditable;                  // ids are known and line up with the items
    private int _removeOffset;   // first item (0-based) of the page shown in delete mode
    private long _removeSeed;   // seed of the view page delete mode works on; 0 = ordered view, >0 = random view

    // "Remove media" preview state (in memory only)
    private readonly List<(int Number, MediaItem Item)> _pending = new(); // selected items in album order
    private readonly HashSet<long> _pendingKept = new();                   // media ids the owner chose to keep
    private long _pendingAlbumId;
    private int _previewOffset;   // first selected item (0-based) on the preview page that is on screen
    private int _promptMsgId;     // message id of the "type the numbers" prompt (0 = none)

    public AlbumsForm(AlbumService albums)
    {
        _albums = albums;
    }

    private long UserId => Device.DeviceId; // private chat: chat id == user id
    private bool IsPrivate => !Device.IsGroup && !Device.IsChannel;

    public override Task Render(MessageResult message) => Task.CompletedTask;

    // =============================================================== text
    public override async Task Load(MessageResult message)
    {
        if (message.IsAction || !IsPrivate) return;
        if (message.UpdateData.Type != UpdateType.Message) return; // ignore edits etc.

        if (message.IsBotCommand)
        {
            await OnCommand(message);
            return;
        }

        var text = message.MessageText?.Trim();
        if (string.IsNullOrEmpty(text)) return; // media messages are handled in SentData

        switch (_mode)
        {
            case Mode.NewTitle:
            {
                var title = CleanTitle(text);
                if (title.Length == 0)
                {
                    await Say("Title can't be empty. Try again or /cancel.");
                    return;
                }

                _mode = Mode.Idle;
                var album = await _albums.CreateAlbum(UserId, title);
                var (card, bf) = await BuildCard(album);
                await Say("✅ Album created.\n\n" + card, bf);
                return;
            }

            case Mode.Rename:
            {
                var title = CleanTitle(text);
                if (title.Length == 0)
                {
                    await Say("Title can't be empty. Try again or /cancel.");
                    return;
                }

                _mode = Mode.Idle;
                var album = await _albums.GetAlbum(_albumId);
                if (album == null || album.OwnerId != UserId) return;
                await _albums.Rename(album.Id, title);
                album.Title = title;
                var (card, bf) = await BuildCard(album);
                await Say(card, bf);
                return;
            }

            case Mode.Adding:
                await Say("Only photos and videos can be added. Press Done when finished.");
                return;
            
            case Mode.Removing:
                await OnRemoveNumbers(message, text);
                return;

            case Mode.RemovePrompt:
                await OnRemovePromptText(message, text);
                return;

            default:
                var code = text.ToUpperInvariant();
                if (AlbumService.LooksLikeCode(code))
                {
                    await TryJoin(code);
                }
                else
                {
                    await Say("Send me an access code to open an album, or use the menu.", MenuButtons());
                }

                return;
        }
    }

    private async Task OnCommand(MessageResult m)
    {
        _mode = Mode.Idle;
        var args = m.BotCommandParameters;

        switch (m.BotCommand)
        {
            case "/start" when args.Count > 0: // deep link: t.me/<bot>?start=<code>
                await TryJoin(args[0]);
                break;

            case "/new":
                await StartNew();
                break;

            case "/join":
                if (args.Count == 0) await Say("Usage: /join CODE");
                else await TryJoin(args[0]);
                break;

            case "/cancel":
                await Say("Cancelled.", MenuButtons());
                break;

            default: // /start, /menu and anything unknown
                await Say(Welcome, MenuButtons());
                break;
        }
    }

    private async Task StartNew()
    {
        _mode = Mode.NewTitle;
        await Say("Send the album title (or /cancel).");
    }

    private async Task TryJoin(string raw)
    {
        if (_albums.IsThrottled(UserId))
        {
            await Say("⏳ Too many wrong codes. Please try again in a few minutes.");
            return;
        }

        var code = raw.Trim().ToUpperInvariant();
        var album = AlbumService.LooksLikeCode(code) ? await _albums.JoinByCode(UserId, code) : null;
        if (album == null)
        {
            _albums.RegisterFailedAttempt(UserId);
            await Say("❌ Wrong code, or the album is closed.");
            return;
        }

        var (card, bf) = await BuildCard(album);
        await Say("✅ Access granted.\n\n" + card, bf);
    }

    // ============================================================== media
    public override async Task SentData(DataResult data)
    {
        if (!IsPrivate) return;

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
            if (_mode == Mode.Adding) await Say("Only photos and videos can be added. Press Done when finished.");
            return;
        }

        if (_mode != Mode.Adding)
        {
            if (FirstOfGroup(msg)) await Say("To add media, open one of your albums and press ➕ Add media.", MenuButtons());
            return;
        }

        var album = await _albums.GetAlbum(_albumId);
        if (album == null || album.OwnerId != UserId)
        {
            _mode = Mode.Idle;
            return;
        }

        var added = await _albums.AddMedia(album.Id, kind, fileId, uniqueId);
        if (added) _added++;
        else _skipped++;

        // Telegram applies a reaction on any item of a media group to the first message of the group,
        // so react once per group.
        if (msg.MediaGroupId == null) await React(msg.MessageId, added ? "👍" : "🤔");
        else if (FirstOfGroup(msg)) await React(msg.MessageId, "👍");
    }

    private bool FirstOfGroup(Message msg)
    {
        if (msg.MediaGroupId == null) return true;
        if (msg.MediaGroupId == _lastGroupId) return false;
        _lastGroupId = msg.MediaGroupId;
        return true;
    }

    private async Task React(int messageId, string emoji)
    {
        try
        {
            await Device.Api(a => a.SetMessageReaction(
                Device.DeviceId, messageId, new ReactionType[] { new ReactionTypeEmoji { Emoji = emoji } }));
        }
        catch (ApiRequestException)
        {
            // reactions are a nicety; ignore failures
        }
    }

    // ============================================================ buttons
    public override async Task Action(MessageResult m)
    {
        var data = m.RawData;
        if (string.IsNullOrEmpty(data) || !IsPrivate) return;
        m.Handled = true;

        var p = data.Split(':');
        try
        {
            switch (p[0])
            {
                case "menu":
                    _mode = Mode.Idle;
                    await m.ConfirmAction();
                    await Show(m, Welcome, MenuButtons());
                    break;

                case "new":
                    await m.ConfirmAction();
                    await StartNew();
                    break;

                case "mine":
                case "shared":
                    _mode = Mode.Idle;
                    await m.ConfirmAction();
                    await ShowList(m, p[0], Int(p, 1));
                    break;

                case "al":
                {
                    _mode = Mode.Idle;
                    var album = await ViewableAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    await ClearView(m.MessageId);
                    var (card, bf) = await BuildCard(album);
                    await Show(m, card, bf);
                    break;
                }

                case "view":
                case "vp":
                case "rnd":
                case "rp":
                    await OnPageView(m, p);
                    break;
                
                case "add":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    _mode = Mode.Adding;
                    _albumId = album.Id;
                    _added = _skipped = 0;
                    var bf = new ButtonForm();
                    bf.AddButtonRow("✅ Done", $"done:{album.Id}");
                    await Say(
                        $"📥 Adding to <b>{H(album.Title)}</b>.\n" +
                        "Send or forward photos and videos – as many as you like. " +
                        "Duplicates are skipped automatically.\nPress <b>Done</b> when finished.", bf);
                    break;
                }

                case "done":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    await ClearView(m.MessageId);
                    var summary = _mode == Mode.Adding && _albumId == album.Id
                        ? $"✅ Saved: {_added} added" + (_skipped > 0 ? $", {_skipped} duplicates skipped" : "") + ".\n\n"
                        : "";
                    _mode = Mode.Idle;
                    var (card, bf) = await BuildCard(album);
                    await Show(m, summary + card, bf);
                    break;
                }

                case "rmlist":                       // card button "🗑 Remove media" (3rd segment is ignored now)
                    await OnRemovePrompt(m, p);
                    break;

                case "rmt":                          // rmt:<mediaId>:<offset>  toggle keep/delete
                case "rmp":                          // rmp:<album>:<offset>    preview page
                case "rmy":                          // rmy:<album>             confirm deletion
                case "rmc":                          // rmc:<album>             cancel
                    await OnRemovePreviewAction(m, p);
                    break;

                case "toggle":
                    await OnToggle(m, p);
                    break;

                case "ren":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    _mode = Mode.Rename;
                    _albumId = album.Id;
                    await Say("Send the new title (or /cancel).");
                    break;
                }

                case "del":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    var bf = new ButtonForm();
                    bf.AddButtonRow(new ButtonBase("Yes, delete", $"delok:{album.Id}"), new ButtonBase("Cancel", $"al:{album.Id}"));
                    await Show(m, $"Delete <b>{H(album.Title)}</b> and all its media references? This can't be undone.", bf);
                    break;
                }

                case "delok":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction("Album deleted.");
                    await _albums.DeleteAlbum(album.Id);
                    await ShowList(m, "mine", 0);
                    break;
                }

                case "leave":
                    await m.ConfirmAction();
                    await _albums.Leave(Long(p, 1), UserId);
                    await ShowList(m, "shared", 0);
                    break;

                default:
                    await m.ConfirmAction();
                    break;
            }
        }
        catch (Exception ex) when (ex is not ApiRequestException)
        {
            await Console.Error.WriteLineAsync($"Action '{data}' failed: {ex}");
            await Say("⚠️ Something went wrong. Please try again.");
        }
    }

    /// <summary>Callback that re-opens a page: seed 0 = ordered view (vp), seed > 0 = random view (rp).</summary>
    private static string PageCb(Album album, long seed, int offset, int flags) =>
        seed == 0 ? $"vp:{album.Id}:{offset}:{flags}" : $"rp:{album.Id}:{seed}:{offset}:{flags}";

    /// <summary>
    /// view:&lt;album&gt;:&lt;offset&gt; | vp:&lt;album&gt;:&lt;offset&gt;:&lt;flags&gt;      ordered view
    /// rnd:&lt;album&gt;           | rp:&lt;album&gt;:&lt;seed&gt;:&lt;offset&gt;:&lt;flags&gt; random view
    /// flags: bit 1 = show numbers, bit 2 = delete mode (delete mode implies numbers). Missing flags = 0.
    /// </summary>
    private async Task OnPageView(MessageResult m, string[] p)
    {
        var album = await ViewableAlbum(m, p);
        if (album == null) return;

        var random = p[0] is "rnd" or "rp";
        var isPage = p[0] is "vp" or "rp";
        var seed = random
            ? (isPage ? Math.Clamp(Long(p, 2), 1, 2147483646) : Random.Shared.NextInt64(1, 2147483646))
            : 0L;
        var offset = isPage || p[0] == "view" ? Math.Max(Int(p, random ? 3 : 2), 0) : 0;
        var flags = isPage ? Int(p, random ? 4 : 3) : 0;
        if ((flags & 2) != 0 && album.OwnerId != UserId) flags &= 1; // only the owner can delete
        if ((flags & 2) != 0) flags |= 1;                            // delete mode always shows numbers

        if (await _albums.MediaCount(album.Id) == 0)
        {
            await m.ConfirmAction("This album is empty.", true);
            return;
        }

        await m.ConfirmAction();

        if ((flags & 2) != 0)
        {
            _mode = Mode.Removing;
            _albumId = album.Id;
            _removeSeed = seed;      // 0 = ordered view
            _removeOffset = offset;
        }
        else if (_mode == Mode.Removing)
        {
            _mode = Mode.Idle;       // left delete mode (Done deleting / Reshuffle / Random / Back to a plain page)
            _removeSeed = 0;
        }
        // Mode.RemovePrompt is deliberately kept: the owner may browse with numbers and then type numbers for the preview.

        await RenderPage(album, seed, offset, flags, m.MessageId, null);
    }

    private async Task RenderPage(Album album, long seed, int offset, int flags, int clickedMessageId, string note)
    {
        var total = await _albums.MediaCount(album.Id);
        if (total == 0)
        {
            await ClearView();
            _mode = Mode.Idle;
            _removeSeed = 0;
            var (card, cardButtons) = await BuildCard(album);
            await Say("✅ The album is now empty.\n\n" + card, cardButtons);
            return;
        }

        if (offset >= total) offset = (total - 1) / ViewPage * ViewPage; // the page vanished after deletions
        if ((flags & 2) != 0) _removeOffset = offset;

        var items = seed == 0
            ? await _albums.ListMedia(album.Id, offset, ViewPage)
            : await _albums.ListMediaShuffled(album.Id, seed, offset, ViewPage);

        List<string> captions = null;
        if ((flags & 1) != 0)
        {
            var pos = await _albums.GetPositions(album.Id, items.Select(i => i.Id));
            captions = NumberCaptions(items.Select(i => pos[i.Id]).ToList());
        }

        var (text, nav) = PageNav(album, seed, offset, total, items.Count, flags, note);
        await ShowViewPage(
            clickedMessageId,
            items,
            failed => text + (failed > 0 ? $"\n⚠️ {failed} item(s) could not be sent." : ""),
            nav,
            captions);
    }

    private (string Text, ButtonForm Buttons) PageNav(Album album, long seed, int offset, int total, int count,
        int flags, string note)
    {
        var deleting = (flags & 2) != 0;
        var numbers = (flags & 1) != 0;

        var text = seed == 0
            ? $"<b>{H(album.Title)}</b>: items {offset + 1}–{offset + count} of {total}"
            : $"🎲 <b>{H(album.Title)}</b>: random {offset + 1}–{offset + count} of {total}";
        if (numbers) text += "\nThe numbers under the pictures are listed in the same order as the pictures (left to right, top to bottom).";
        if (deleting) text += "\n🗑 Type the number(s) to delete, e.g. <code>12</code>, <code>12 15 18</code> or <code>12-15</code>.";
        if (note != null) text = note + "\n\n" + text;

        var bf = new ButtonForm();
        var row = new List<ButtonBase>();
        if (offset > 0)
            row.Add(new ButtonBase("◀ Prev", PageCb(album, seed, Math.Max(offset - ViewPage, 0), flags)));
        if (offset + ViewPage < total)
            row.Add(new ButtonBase("Next ▶", PageCb(album, seed, offset + ViewPage, flags)));
        if (row.Count > 0) bf.AddButtonRow(row.ToArray());

        if (deleting)
        {
            bf.AddButtonRow("✅ Done deleting", PageCb(album, seed, offset, 1));
        }
        else
        {
            var toggle = numbers
                ? new ButtonBase("🔢 Hide numbers", PageCb(album, seed, offset, flags & ~1))
                : new ButtonBase("🔢 Show numbers", PageCb(album, seed, offset, flags | 1));
            var buttons = new List<ButtonBase> { toggle };
            if (album.OwnerId == UserId)
                buttons.Add(new ButtonBase("🗑 Delete by number", PageCb(album, seed, offset, 3)));
            bf.AddButtonRow(buttons.ToArray());
        }

        bf.AddButtonRow(
            new ButtonBase(seed == 0 ? "🎲 Random" : "🎲 Reshuffle", $"rnd:{album.Id}"),
            new ButtonBase("⬅ Back", $"al:{album.Id}"));
        return (text, bf);
    }

    private async Task OnToggle(MessageResult m, string[] p)
    {
        var album = await OwnedAlbum(m, p);
        if (album == null) return;

        string code = null;
        if (album.IsOpen)
        {
            await _albums.CloseAlbum(album.Id);
            await m.ConfirmAction("Album closed. Everyone else lost access.", true);
        }
        else
        {
            code = await _albums.OpenAlbum(album.Id);
            await m.ConfirmAction("Album opened.");
        }

        album = await _albums.GetAlbum(album.Id);
        var (card, bf) = await BuildCard(album);
        await Show(m, card, bf);

        if (code != null)
        {
            await Say(
                $"🔓 <b>{H(album.Title)}</b> is open.\n\n" +
                $"Access code: <code>{code}</code>\n" +
                $"Link: https://t.me/{BotInfo.Username}?start={code}\n\n" +
                "Anyone with the code can watch the album (but not edit it). " +
                "Closing the album invalidates the code and removes all viewers.");
        }
    }

    // ============================================================ screens
    private static ButtonForm MenuButtons()
    {
        var bf = new ButtonForm();
        bf.AddButtonRow("➕ New album", "new");
        bf.AddButtonRow("📁 My albums", "mine:0");
        bf.AddButtonRow("📥 Shared with me", "shared:0");
        return bf;
    }

    private async Task ShowList(MessageResult m, string kind, int page)
    {
        page = Math.Max(page, 0);
        var (items, total) = kind == "mine"
            ? await _albums.ListOwned(UserId, page * ListPage, ListPage)
            : await _albums.ListShared(UserId, page * ListPage, ListPage);

        if (items.Count == 0 && total > 0) // page is out of range after deletions
        {
            page = (total - 1) / ListPage;
            (items, total) = kind == "mine"
                ? await _albums.ListOwned(UserId, page * ListPage, ListPage)
                : await _albums.ListShared(UserId, page * ListPage, ListPage);
        }

        var text = kind == "mine" ? "📁 <b>My albums</b>" : "📥 <b>Shared with me</b>";
        if (total == 0)
        {
            text += "\n\nNothing here yet." + (kind == "shared" ? " Send me an access code to get started." : "");
        }

        var bf = new ButtonForm();
        foreach (var it in items)
        {
            var icon = kind == "mine" ? (it.Album.IsOpen ? "🔓" : "🔒") : "📁";
            bf.AddButtonRow($"{icon} {Truncate(it.Album.Title, 40)} ({it.MediaCount})", $"al:{it.Album.Id}");
        }

        var nav = new List<ButtonBase>();
        if (page > 0) nav.Add(new ButtonBase("◀", $"{kind}:{page - 1}"));
        if ((page + 1) * ListPage < total) nav.Add(new ButtonBase("▶", $"{kind}:{page + 1}"));
        if (nav.Count > 0) bf.AddButtonRow(nav.ToArray());
        if (kind == "mine") bf.AddButtonRow("➕ New album", "new");
        bf.AddButtonRow("⬅ Menu", "menu");

        await Show(m, text, bf);
    }

    private async Task<(string Text, ButtonForm Buttons)> BuildCard(Album album)
    {
        var count = await _albums.MediaCount(album.Id);
        var isOwner = album.OwnerId == UserId;

        var sb = new StringBuilder();
        sb.Append($"📁 <b>{H(album.Title)}</b>\n🖼 Media: {count}");

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

    // ============================================================ helpers
    /// <summary>Sends a new HTML message.</summary>
    private Task<Message> Say(string html, ButtonForm bf = null) =>
        Device.Send(html, bf, parseMode: ParseMode.Html);

    /// <summary>Edits the message the button belongs to; falls back to a new message.</summary>
    private async Task Show(MessageResult m, string html, ButtonForm bf)
    {
        if (m.IsAction)
        {
            try
            {
                await Device.Edit(m.MessageId, html, bf, ParseMode.Html);
                return;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
            {
                return;
            }
            catch (ApiRequestException)
            {
                // message too old / not editable -> send a fresh one
            }
        }

        await Say(html, bf);
    }

    private async Task TryDelete(int messageId)
    {
        try
        {
            await Device.DeleteMessage(messageId);
        }
        catch (ApiRequestException)
        {
        }
    }

    private async Task<int> SendOne(MediaItem item, ButtonForm bf, string caption = null)
    {
        var msg = item.Kind == MediaKind.Photo
            ? await Device.SendPhoto(InputFile.FromFileId(item.FileId), caption, buttons: bf)
            : await Device.SendVideo(InputFile.FromFileId(item.FileId), caption, buttons: bf);
        return msg?.MessageId ?? 0;
    }

    private static string CaptionAt(IReadOnlyList<string> captions, int i) =>
        captions != null && i < captions.Count ? captions[i] : null;
    
    /// <summary>
    /// Telegram shows an album caption under the grid only when exactly ONE item has a caption,
    /// so all numbers (in display order) go into the caption of the first item.
    /// </summary>
    private static List<string> NumberCaptions(IReadOnlyList<int> numbers)
    {
        if (numbers.Count == 0) return null;
        var captions = new List<string>(new string[numbers.Count]); // all null
        captions[0] = numbers.Count == 1
            ? $"#{numbers[0]}"
            : "Numbers in order: " + string.Join("  ", numbers.Select(n => $"#{n}"));
        return captions;
    }

    private static InputMedia ToInput(MediaItem i, string caption) =>
        i.Kind == MediaKind.Photo
            ? new InputMediaPhoto(InputFile.FromFileId(i.FileId)) { Caption = caption }
            : new InputMediaVideo(InputFile.FromFileId(i.FileId)) { Caption = caption };

    private async Task<(List<int> Ids, int Failed)> SendBatch(List<MediaItem> items,
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
                var sent = await Device.Dispatch(a => a.SendMediaGroup(Device.DeviceId, group));
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

    private async Task<bool> TryEditGroup(List<MediaItem> items, IReadOnlyList<string> captions)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (!await EditMedia(_viewIds[i], ToInput(items[i], CaptionAt(captions, i)))) return false;
        }

        return true;
    }

    /// <summary>
    /// Shows one page. clickedMessageId = the message the user clicked (card or navigation message),
    /// or the navigation message id when a typed text triggered the refresh. Edits in place when possible.
    /// </summary>
    private async Task ShowViewPage(int clickedMessageId, List<MediaItem> items, Func<int, string> textFor,
        ButtonForm nav, IReadOnlyList<string> captions = null)
    {
        var clickedNav = _viewNavId != 0 && clickedMessageId == _viewNavId;

        if (clickedNav && _viewEditable && _viewIds.Count == items.Count && await TryEditGroup(items, captions))
        {
            await EditNav(textFor(0), nav);
            return;
        }

        await ClearView();
        if (!clickedNav && clickedMessageId != 0) await TryDelete(clickedMessageId); // the card / a stale nav

        var (ids, failed) = await SendBatch(items, captions);
        _viewIds.AddRange(ids);
        _viewEditable = failed == 0 && ids.Count == items.Count;
        var navMsg = await Say(textFor(failed), nav);
        _viewNavId = navMsg?.MessageId ?? 0;
    }

    /// <summary>Edits the navigation message of the current view (or sends one if there is none).</summary>
    private async Task EditNav(string html, ButtonForm bf)
    {
        if (_viewNavId == 0)
        {
            _viewNavId = (await Say(html, bf))?.MessageId ?? 0;
            return;
        }

        try
        {
            await Device.Edit(_viewNavId, html, bf, ParseMode.Html);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
        {
        }
        catch (ApiRequestException ex)
        {
            await Console.Error.WriteLineAsync($"Could not edit navigation message: {ex.Message}");
        }
    }

    private async Task ClearView(int keepMessageId = 0)
    {
        var all = new List<int>(_viewIds);
        if (_viewNavId != 0 && _viewNavId != keepMessageId) all.Add(_viewNavId);
        _viewIds.Clear();
        _viewNavId = 0;
        _viewEditable = false;
        if (all.Count == 0) return;

        try
        {
            await Device.Dispatch(a => a.DeleteMessages(Device.DeviceId, all));
        }
        catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Could not delete old view: {ex.Message}");
        }
    }

    private async Task<bool> EditMedia(int messageId, InputMedia media)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await Device.Dispatch(a => a.EditMessageMedia(Device.DeviceId, messageId, media));
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
    
    private async Task OnRemoveNumbers(MessageResult message, string text)
    {
        var album = await _albums.GetAlbum(_albumId);
        if (album == null || album.OwnerId != UserId)
        {
            _mode = Mode.Idle;
            return;
        }

        await TryDelete(message.MessageId); // keep the chat tidy: remove the typed message

        var positions = ParsePositions(text);
        var total = await _albums.MediaCount(album.Id);
        string note;
        var deleted = false;

        if (positions == null || positions.Count == 0)
        {
            note = "⚠️ I couldn't read that.";
        }
        else
        {
            var valid = positions.Where(n => n >= 1 && n <= total).ToList();
            var ids = await _albums.GetMediaIdsAtPositions(album.Id, valid); // resolve before deleting
            if (ids.Count == 0)
            {
                note = "⚠️ No items with those numbers.";
            }
            else
            {
                await _albums.DeleteMedia(ids);
                note = $"✅ Deleted {ids.Count} item(s)" +
                       (valid.Count < positions.Count ? " (some numbers were out of range)." : ".");
                deleted = true;
            }
        }

        if (deleted)
        {
            await RenderPage(album, _removeSeed, _removeOffset, 3, _viewNavId, note); // refresh in place
            return;
        }

        var count = Math.Min(ViewPage, Math.Max(total - _removeOffset, 0));
        var (navText, navButtons) = PageNav(album, _removeSeed, _removeOffset, total, count, 3, note);
        await EditNav(navText, navButtons);
    }

    // ----- Remove media: prompt -> preview -> confirm -----------------------------------------------

    private void ClearPending()
    {
        _pending.Clear();
        _pendingKept.Clear();
        _pendingAlbumId = 0;
        _previewOffset = 0;
        _promptMsgId = 0;
    }

    /// <summary>Card button "🗑 Remove media": turns the card message into the "type numbers" prompt.</summary>
    private async Task OnRemovePrompt(MessageResult m, string[] p)
    {
        var album = await OwnedAlbum(m, p);
        if (album == null) return;

        if (await _albums.MediaCount(album.Id) == 0)
        {
            await m.ConfirmAction("Nothing to remove.", true);
            return;
        }

        await m.ConfirmAction();
        await ClearView(m.MessageId);   // drop any album still on screen, keep the clicked message
        ClearPending();
        _mode = Mode.RemovePrompt;
        _albumId = album.Id;
        _promptMsgId = m.MessageId;
        await RenderRemovePrompt(album, null);
    }

    private async Task RenderRemovePrompt(Album album, string note)
    {
        var total = await _albums.MediaCount(album.Id);
        var text = $"🗑 <b>Remove media</b> – {H(album.Title)} ({total} items)\n\n" +
                   "Type the numbers of the items to delete, e.g. <code>5</code>, <code>1 2 3</code> or <code>1-3</code>.\n" +
                   "You will see a preview first and can cancel before anything is deleted.\n" +
                   "Don't know the numbers? Open the view with numbers.";
        if (note != null) text = note + "\n\n" + text;

        var bf = new ButtonForm();
        bf.AddButtonRow("👀 View with numbers", $"vp:{album.Id}:0:1");
        bf.AddButtonRow("⬅ Cancel", $"al:{album.Id}");

        if (_promptMsgId != 0)
        {
            try
            {
                await Device.Edit(_promptMsgId, text, bf, ParseMode.Html);
                return;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
            {
                return;
            }
            catch (ApiRequestException)
            {
                // prompt message is gone -> send a new one
            }
        }

        _promptMsgId = (await Say(text, bf))?.MessageId ?? 0;
    }

    /// <summary>Typed numbers in Mode.RemovePrompt: build (or replace) the selection and show the preview. Deletes nothing.</summary>
    private async Task OnRemovePromptText(MessageResult message, string text)
    {
        var album = await _albums.GetAlbum(_albumId);
        if (album == null || album.OwnerId != UserId)
        {
            _mode = Mode.Idle;
            return;
        }

        await TryDelete(message.MessageId); // keep the chat tidy

        var positions = ParsePositions(text);
        if (positions == null || positions.Count == 0)
        {
            await RefreshRemoveScreen(album, "⚠️ I couldn't read that. Examples: 5, 1 2 3, 1-3");
            return;
        }

        var resolved = await _albums.ResolvePositions(album.Id, positions);
        if (resolved.Count == 0)
        {
            await RefreshRemoveScreen(album, "⚠️ No items with those numbers.");
            return;
        }

        _pending.Clear();
        _pendingKept.Clear();
        _pending.AddRange(resolved);
        _pendingAlbumId = album.Id;

        var skipped = positions.Count - resolved.Count;
        var note = skipped > 0 ? $"ℹ️ {skipped} number(s) are out of range and were ignored." : null;
        await RenderRemovePreview(album, 0, _viewNavId != 0 ? _viewNavId : _promptMsgId, note);
    }

    /// <summary>Shows a one-line note without redrawing media: on the preview nav if one is on screen, else on the prompt.</summary>
    private async Task RefreshRemoveScreen(Album album, string note)
    {
        if (_pending.Count > 0 && _viewNavId != 0)
        {
            var page = _pending.Skip(_previewOffset).Take(ViewPage).ToList();
            var (text, bf) = RemovePreviewNav(album, _previewOffset, page, note);
            await EditNav(text, bf);
            return;
        }

        await RenderRemovePrompt(album, note);
    }

    private async Task RenderRemovePreview(Album album, int offset, int clickedMessageId, string note)
    {
        var lastPageStart = Math.Max(_pending.Count - 1, 0) / ViewPage * ViewPage;
        offset = Math.Clamp(offset, 0, lastPageStart);
        _previewOffset = offset;

        var page = _pending.Skip(offset).Take(ViewPage).ToList();
        var items = page.Select(x => x.Item).ToList();
        var captions = NumberCaptions(page.Select(x => x.Number).ToList());
        var (text, nav) = RemovePreviewNav(album, offset, page, note);

        await ShowViewPage(
            clickedMessageId,
            items,
            failed => text + (failed > 0 ? $"\n⚠️ {failed} item(s) could not be shown (they can still be deleted)." : ""),
            nav,
            captions);
        _promptMsgId = 0; // the prompt message was replaced by the preview
    }

    private (string Text, ButtonForm Buttons) RemovePreviewNav(Album album, int offset,
        List<(int Number, MediaItem Item)> page, string note)
    {
        var toDelete = _pending.Count(x => !_pendingKept.Contains(x.Item.Id));

        var text = $"🗑 <b>Preview</b> – {H(album.Title)}\n" +
                   $"Selected {_pending.Count}, showing {offset + 1}–{offset + page.Count}.\n" +
                   "The numbers under the pictures are listed in picture order.\n" +
                   "Tap a number to keep it (↩) or to delete it again (🗑). Type other numbers to replace the selection.\n" +
                   $"<b>Will be deleted: {toDelete} of {_pending.Count}</b>";
        if (note != null) text = note + "\n\n" + text;

        var bf = new ButtonForm();
        var row = new List<ButtonBase>();
        foreach (var (number, item) in page)
        {
            var kept = _pendingKept.Contains(item.Id);
            row.Add(new ButtonBase($"{(kept ? "↩" : "🗑")} #{number}", $"rmt:{item.Id}:{offset}"));
            if (row.Count == 5)
            {
                bf.AddButtonRow(row.ToArray());
                row = new List<ButtonBase>();
            }
        }

        if (row.Count > 0) bf.AddButtonRow(row.ToArray());

        var navRow = new List<ButtonBase>();
        if (offset > 0) navRow.Add(new ButtonBase("◀ Prev", $"rmp:{album.Id}:{Math.Max(offset - ViewPage, 0)}"));
        if (offset + ViewPage < _pending.Count) navRow.Add(new ButtonBase("Next ▶", $"rmp:{album.Id}:{offset + ViewPage}"));
        if (navRow.Count > 0) bf.AddButtonRow(navRow.ToArray());

        var actions = new List<ButtonBase>();
        if (toDelete > 0) actions.Add(new ButtonBase($"🗑 Delete {toDelete}", $"rmy:{album.Id}"));
        actions.Add(new ButtonBase("✖ Cancel", $"rmc:{album.Id}"));
        bf.AddButtonRow(actions.ToArray());

        return (text, bf);
    }

    /// <summary>The preview is live only for the owner, in Mode.RemovePrompt, with a non-empty selection of that album.</summary>
    private async Task<Album> LivePreview(MessageResult m, long albumId)
    {
        if (_mode != Mode.RemovePrompt || _pending.Count == 0 || m.MessageId != _viewNavId ||
            (albumId != 0 && _pendingAlbumId != albumId))
        {
            await m.ConfirmAction("This preview has expired. Start again from 🗑 Remove media.", true);
            return null;
        }

        var album = await _albums.GetAlbum(_pendingAlbumId);
        if (album == null || album.OwnerId != UserId)
        {
            await m.ConfirmAction("Album not found or you are not its owner.", true);
            return null;
        }

        return album;
    }

    private async Task OnRemovePreviewAction(MessageResult m, string[] p)
    {
        var album = await LivePreview(m, p[0] == "rmt" ? 0 : Long(p, 1));
        if (album == null) return;

        switch (p[0])
        {
            case "rmt": // toggle keep / delete: only the navigation message changes (no media edits)
            {
                var id = Long(p, 1);
                if (_pending.All(x => x.Item.Id != id))
                {
                    await m.ConfirmAction("This preview has expired. Start again from 🗑 Remove media.", true);
                    return;
                }

                await m.ConfirmAction();
                if (!_pendingKept.Remove(id)) _pendingKept.Add(id);
                _previewOffset = Math.Max(Int(p, 2), 0);
                await RefreshRemoveScreen(album, null);
                break;
            }

            case "rmp": // preview page
                await m.ConfirmAction();
                await RenderRemovePreview(album, Int(p, 2), m.MessageId, null);
                break;

            case "rmc": // cancel everything
            {
                await m.ConfirmAction("Cancelled – nothing was deleted.");
                ClearPending();
                _mode = Mode.Idle;
                await ClearView(m.MessageId);
                var (card, bf) = await BuildCard(album);
                await Show(m, card, bf);
                break;
            }

            case "rmy": // confirm: delete everything that was not kept
            {
                var ids = _pending.Where(x => !_pendingKept.Contains(x.Item.Id)).Select(x => x.Item.Id).ToList();
                if (ids.Count == 0)
                {
                    await m.ConfirmAction("Nothing selected to delete.", true);
                    return;
                }

                await m.ConfirmAction();
                var deleted = await _albums.DeleteMediaInAlbum(album.Id, ids);
                ClearPending();
                _mode = Mode.Idle;
                await ClearView(m.MessageId);
                var (card, bf) = await BuildCard(album);
                await Show(m, $"✅ Deleted {deleted} item(s).\n\n{card}", bf);
                break;
            }
        }
    }

    /// <summary>"12", "12 15 18", "12-15", "3, 7-9" -> set of numbers; null if unreadable.</summary>
    private static SortedSet<int> ParsePositions(string text)
    {
        var set = new SortedSet<int>();
        foreach (var token in text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('-');
            if (parts.Length == 1 && int.TryParse(parts[0], out var one))
            {
                set.Add(one);
            }
            else if (parts.Length == 2 && int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi)
                     && lo <= hi && hi - lo < 100)
            {
                for (var n = lo; n <= hi; n++) set.Add(n);
            }
            else
            {
                return null;
            }

            if (set.Count > 100) return null;
        }

        return set;
    }

    private async Task<Album> OwnedAlbum(MessageResult m, string[] p)
    {
        var album = await _albums.GetAlbum(Long(p, 1));
        if (album != null && album.OwnerId == UserId) return album;
        await m.ConfirmAction("Album not found or you are not its owner.", true);
        return null;
    }

    private async Task<Album> ViewableAlbum(MessageResult m, string[] p)
    {
        var album = await _albums.GetAlbum(Long(p, 1));
        if (album != null && await _albums.CanView(album, UserId)) return album;
        await m.ConfirmAction("This album is unavailable.", true);
        return null;
    }

    private static string H(string s) => WebUtility.HtmlEncode(s ?? "");

    private static string CleanTitle(string s) =>
        Truncate(string.Join(' ', s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)), 100);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static int Int(string[] p, int i) => p.Length > i && int.TryParse(p[i], out var v) ? v : 0;

    private static long Long(string[] p, int i) => p.Length > i && long.TryParse(p[i], out var v) ? v : 0;
}