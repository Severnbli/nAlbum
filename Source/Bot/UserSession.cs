/*
    nAlbum – telegram bot for sharing photos and videos in albums
    Copyright (C) 2026 Usevalad Buben usevaladbuben@gmail.com

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as published by
    the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
    or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

using System.Collections.Concurrent;
using nAlbum.Data;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace nAlbum.Bot;

// Values are stored in saved sessions: never renumber, leave a gap when a mode is removed (6 was GoToNumber).
public enum Mode
{
    Idle = 0, NewTitle = 1, Rename = 2, Adding = 3, Removing = 4, RemovePrompt = 5,
    InviteForward = 7, SongUpload = 8, Broadcast = 9, InviteExpiry = 10, InviteUses = 11
}

public sealed class UserSession
{
    public Mode Mode { get; set; } = Mode.Idle;
    public long AlbumId { get; set; }                 // was _albumId
    public long InviteSeconds { get; set; }           // lifetime picked for the invite link being created, 0 = no limit
    public int MenuMessageId { get; set; }            // the single message with menu buttons (card, list, prompt)
    public int BroadcastMessageId { get; set; }       // admin's message waiting for broadcast confirmation
    public BroadcastDraft Broadcast { get; set; }     // its content, sent to everyone on confirmation
    public int SongMessageId { get; set; }            // album song on screen; removed when the user leaves that album
    public long SongAlbumId { get; set; }
    public AddState Add { get; } = new();
    public ViewState View { get; } = new();
    public RemovalState Removal { get; } = new();
}

public sealed class AddState
{
    public int Added;                 // was _added
    public int Skipped;               // was _skipped
    public HashSet<string> SeenMediaGroups { get; } = new();
    public Dictionary<string, int> ReactedMediaGroups { get; } = new();   // media group id -> lowest message id reacted to
    public int Removed;               // media deleted from the chat before Done
    public int PromptMessageId;
    public int KeyboardMessageId;
    public string PromptText;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public ConcurrentDictionary<int, PendingMedia> Pending { get; } = new();   // message id -> media waiting for Done
    public ConcurrentDictionary<int, byte> UnsupportedHandled { get; } = new();
    public ConcurrentDictionary<int, byte> UserMessages { get; } = new();

    public void Reset()
    {
        Added = Skipped = Removed = PromptMessageId = KeyboardMessageId = 0;
        PromptText = null;
        SeenMediaGroups.Clear();
        ReactedMediaGroups.Clear();
        Pending.Clear();
        UnsupportedHandled.Clear();
        UserMessages.Clear();
    }
}

/// <summary>
/// Media the owner sent to pick items for deletion. A media group arrives as several messages,
/// so picks are collected and handled once, after a short quiet period.
/// </summary>
public sealed class MediaPick
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, MediaItem> _matched = new();
    private int _missing;
    private int _ticket;

    /// <summary>Records a pick (item == null: not in the album) and returns the ticket of this call.</summary>
    public async Task<int> Add(MediaItem item)
    {
        await _gate.WaitAsync();
        try
        {
            if (item == null) _missing++;
            else _matched[item.Id] = item;
            return ++_ticket;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns everything collected, but only for the latest ticket; null for superseded calls.</summary>
    public async Task<(List<MediaItem> Items, int Missing)?> Take(int ticket)
    {
        await _gate.WaitAsync();
        try
        {
            if (ticket != _ticket) return null;
            var result = (_matched.Values.OrderBy(i => i.Number).ToList(), _missing);
            _matched.Clear();
            _missing = 0;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public enum BroadcastKind { Text, Photo, Video, Animation, Document, Audio, Voice, VideoNote, Sticker, Copy }

/// <summary>
/// Broadcast content taken from the admin's message. Media is resent by its Telegram file id;
/// other message types (polls, locations, ...) are copied from the admin's chat.
/// </summary>
public sealed class BroadcastDraft
{
    public BroadcastKind Kind { get; set; }
    public string FileId { get; set; }
    public string Text { get; set; }                  // text or caption
    public MessageEntity[] Entities { get; set; }     // formatting of Text, kept as is
    public LinkPreviewOptions LinkPreview { get; set; }
    public bool CaptionAbove { get; set; }
    public bool Spoiler { get; set; }

    public static BroadcastDraft From(Message msg)
    {
        var d = new BroadcastDraft
        {
            Text = msg.Caption, Entities = msg.CaptionEntities, CaptionAbove = msg.ShowCaptionAboveMedia, Spoiler = msg.HasMediaSpoiler
        };
        switch (msg.Type)
        {
            case MessageType.Text:
                d.Kind = BroadcastKind.Text;
                d.Text = msg.Text;
                d.Entities = msg.Entities;
                d.LinkPreview = msg.LinkPreviewOptions;
                break;
            case MessageType.Photo when msg.Photo is { Length: > 0 }:
                d.Kind = BroadcastKind.Photo;
                d.FileId = msg.Photo[^1].FileId; // largest size
                break;
            case MessageType.Video:
                d.Kind = BroadcastKind.Video;
                d.FileId = msg.Video.FileId;
                break;
            case MessageType.Animation:
                d.Kind = BroadcastKind.Animation;
                d.FileId = msg.Animation.FileId;
                break;
            case MessageType.Document:
                d.Kind = BroadcastKind.Document;
                d.FileId = msg.Document.FileId;
                break;
            case MessageType.Audio:
                d.Kind = BroadcastKind.Audio;
                d.FileId = msg.Audio.FileId;
                break;
            case MessageType.Voice:
                d.Kind = BroadcastKind.Voice;
                d.FileId = msg.Voice.FileId;
                break;
            case MessageType.VideoNote:
                d.Kind = BroadcastKind.VideoNote;
                d.FileId = msg.VideoNote.FileId;
                break;
            case MessageType.Sticker:
                d.Kind = BroadcastKind.Sticker;
                d.FileId = msg.Sticker.FileId;
                break;
            default:
                d.Kind = BroadcastKind.Copy;
                break;
        }

        return d;
    }
}

public sealed record PendingMedia(MediaKind Kind, string FileId, string UniqueId, bool InGroup);

public sealed class ViewState
{
    public List<int> Ids { get; } = new();   // was _viewIds   (message ids of the album on screen)
    public int NavId;                        // was _viewNavId
    public bool Editable;                    // was _viewEditable
    public string LastPageKey;               // new (Part 4): "<album>:<seed>:<offset>"
    public long DeleteSeed;                  // was _removeSeed   (0 = ordered view)
    public int DeleteOffset;                 // was _removeOffset
    public int DeleteFlags = 3;              // view flags to keep while refreshing in delete mode
}

public sealed class RemovalState
{
    public MediaPick Pick { get; } = new();
    public List<(int Number, MediaItem Item)> Pending { get; } = new(); // was _pending
    public HashSet<long> Kept { get; } = new();                          // was _pendingKept
    public long AlbumId;                                                 // was _pendingAlbumId
    public int PreviewOffset;                                            // was _previewOffset
    public int PromptMsgId;                                              // was _promptMsgId

    public void Clear() { Pending.Clear(); Kept.Clear(); AlbumId = 0; PreviewOffset = 0; PromptMsgId = 0; } // was ClearPending()
}
