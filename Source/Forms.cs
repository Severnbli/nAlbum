using System.Net;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotBase.Base;
using TelegramBotBase.Form;

namespace nAlbum.Source;

/// <summary>
/// The only form of the bot (one instance per user session). All rendering is done explicitly in
/// Load / SentData / Action, so Render() is intentionally empty.
///
/// Callback data:
///   menu | new | mine:&lt;page&gt; | shared:&lt;page&gt; | al:&lt;album&gt;           navigation
///   view:&lt;album&gt;:&lt;offset&gt; | vp:&lt;album&gt;:&lt;offset&gt;                 watch media (vp = "page" button)
///   add:&lt;album&gt; | done:&lt;album&gt;                                  add-media mode
///   rmlist:&lt;album&gt;:&lt;cursor&gt; | rmn:&lt;album&gt;:&lt;cursor&gt; | rm:&lt;media&gt;  remove media
///   toggle | ren | del | delok :&lt;album&gt;                          owner actions
///   leave:&lt;album&gt;                                               viewer leaves a shared album
/// Every owner action re-checks ownership on the server side.
/// </summary>
public class AlbumsForm : FormBase
{
    private const int ListPage = 8;    // albums per page in lists
    private const int ViewPage = 10;   // media per page when watching (= one Telegram media group)
    private const int RemovePage = 5;  // media per page in "remove" mode

    private const string Welcome =
        "👋 <b>Albums bot</b>\n\n" +
        "Create albums of photos and videos and share them with a secret code.\n" +
        "Got a code from a friend? Just send it to me.";

    private enum Mode { Idle, NewTitle, Rename, Adding }

    private readonly AlbumService _albums;

    private Mode _mode = Mode.Idle;
    private long _albumId;          // album being renamed / filled
    private int _added, _skipped;   // counters of the current "add media" session
    private string _lastGroupId;    // last media-group id we already reacted to

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
                    var (card, bf) = await BuildCard(album);
                    await Show(m, card, bf);
                    break;
                }

                case "view":
                case "vp":
                    await OnView(m, p);
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
                        "Send or forward photos and videos — as many as you like. " +
                        "Duplicates are skipped automatically.\nPress <b>Done</b> when finished.", bf);
                    break;
                }

                case "done":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    var summary = _mode == Mode.Adding && _albumId == album.Id
                        ? $"✅ Saved: {_added} added" + (_skipped > 0 ? $", {_skipped} duplicates skipped" : "") + ".\n\n"
                        : "";
                    _mode = Mode.Idle;
                    var (card, bf) = await BuildCard(album);
                    await Show(m, summary + card, bf);
                    break;
                }

                case "rmlist":
                case "rmn":
                    await OnRemoveList(m, p);
                    break;

                case "rm":
                    await OnRemoveMedia(m, p);
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
            Console.Error.WriteLine($"Action '{data}' failed: {ex}");
            await Say("⚠️ Something went wrong. Please try again.");
        }
    }

    private async Task OnView(MessageResult m, string[] p)
    {
        var album = await ViewableAlbum(m, p);
        if (album == null) return;

        var offset = Math.Max(Int(p, 2), 0);
        var total = await _albums.MediaCount(album.Id);
        var items = await _albums.ListMedia(album.Id, offset, ViewPage);
        if (items.Count == 0)
        {
            await m.ConfirmAction("This album is empty.", true);
            return;
        }

        await m.ConfirmAction();
        if (p[0] == "vp") await TryDelete(m.MessageId); // replace the old navigation message

        await SendBatch(items);

        var bf = new ButtonForm();
        var row = new List<ButtonBase>();
        if (offset > 0) row.Add(new ButtonBase("◀ Prev", $"vp:{album.Id}:{Math.Max(offset - ViewPage, 0)}"));
        if (offset + ViewPage < total) row.Add(new ButtonBase("Next ▶", $"vp:{album.Id}:{offset + ViewPage}"));
        if (row.Count > 0) bf.AddButtonRow(row.ToArray());
        bf.AddButtonRow("⬅ Back to album", $"al:{album.Id}");

        await Say($"{H(album.Title)}: items {offset + 1}–{offset + items.Count} of {total}", bf);
    }

    private async Task OnRemoveList(MessageResult m, string[] p)
    {
        var album = await OwnedAlbum(m, p);
        if (album == null) return;

        var items = await _albums.ListMediaAfter(album.Id, Long(p, 2), RemovePage + 1);
        if (items.Count == 0)
        {
            await m.ConfirmAction("Nothing to remove.", true);
            return;
        }

        await m.ConfirmAction();
        if (p[0] == "rmn") await TryDelete(m.MessageId);

        var hasMore = items.Count > RemovePage;
        items = items.Take(RemovePage).ToList();

        foreach (var item in items)
        {
            var bf = new ButtonForm();
            bf.AddButtonRow("🗑 Remove", $"rm:{item.Id}");
            try
            {
                await SendOne(item, bf);
            }
            catch (ApiRequestException) // broken file id: still let the owner remove it
            {
                await Say($"⚠️ Unavailable item #{item.Id}", bf);
            }
        }

        var nav = new ButtonForm();
        if (hasMore) nav.AddButtonRow("Next ▶", $"rmn:{album.Id}:{items[^1].Id}");
        nav.AddButtonRow("⬅ Back to album", $"al:{album.Id}");
        await Say("Tap 🗑 under an item to remove it from the album.", nav);
    }

    private async Task OnRemoveMedia(MessageResult m, string[] p)
    {
        var media = await _albums.GetMedia(Long(p, 1));
        var album = media == null ? null : await _albums.GetAlbum(media.AlbumId);
        if (album == null || album.OwnerId != UserId)
        {
            await m.ConfirmAction("Item not found or you are not the owner.", true);
            return;
        }

        await _albums.DeleteMedia(media.Id);
        await m.ConfirmAction("Removed");
        await TryDelete(m.MessageId);
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
            bf.AddButtonRow("👀 View", $"view:{album.Id}:0");
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
            sb.Append("\n🔒 Closed — only you can see it");
        }

        bf.AddButtonRow(new ButtonBase("👀 View", $"view:{album.Id}:0"), new ButtonBase("➕ Add media", $"add:{album.Id}"));
        bf.AddButtonRow(new ButtonBase("🗑 Remove media", $"rmlist:{album.Id}:0"), new ButtonBase("✏️ Rename", $"ren:{album.Id}"));
        bf.AddButtonRow(album.IsOpen ? "🔒 Close album" : "🔓 Open for others", $"toggle:{album.Id}");
        bf.AddButtonRow("❌ Delete album", $"del:{album.Id}");
        bf.AddButtonRow("⬅ My albums", "mine:0");
        return (sb.ToString(), bf);
    }

    // ============================================================ helpers
    /// <summary>Sends a new HTML message.</summary>
    private Task Say(string html, ButtonForm bf = null) =>
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

    private async Task SendOne(MediaItem item, ButtonForm bf)
    {
        if (item.Kind == MediaKind.Photo)
            await Device.SendPhoto(InputFile.FromFileId(item.FileId), buttons: bf);
        else
            await Device.SendVideo(InputFile.FromFileId(item.FileId), buttons: bf);
    }

    /// <summary>Sends up to 10 items as one media group (falls back to one by one).</summary>
    private async Task SendBatch(List<MediaItem> items)
    {
        if (items.Count >= 2)
        {
            try
            {
                var group = items
                    .Select(i => i.Kind == MediaKind.Photo
                        ? (IAlbumInputMedia)new InputMediaPhoto(InputFile.FromFileId(i.FileId))
                        : new InputMediaVideo(InputFile.FromFileId(i.FileId)))
                    .ToList();
                await Device.Api(a => a.SendMediaGroup(Device.DeviceId, group));
                return;
            }
            catch (ApiRequestException)
            {
                // fall through and try the items individually
            }
        }

        foreach (var item in items)
        {
            try
            {
                await SendOne(item, null);
            }
            catch (ApiRequestException)
            {
                Console.Error.WriteLine($"Media {item.Id} could not be sent");
            }
        }
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