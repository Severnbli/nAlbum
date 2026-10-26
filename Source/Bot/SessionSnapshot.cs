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

using System.Text.Json;
using nAlbum.Data;

namespace nAlbum.Bot;

/// <summary>
/// Serializable copy of <see cref="UserSession"/>, saved by the JSON state machine on shutdown and restored on start,
/// so menus, views, songs and unfinished flows survive a restart. Locks and media-pick buffers are not saved.
/// </summary>
public sealed class SessionSnapshot
{
    public const string Key = "session";

    public Mode Mode { get; set; }
    public long AlbumId { get; set; }
    public int MenuMessageId { get; set; }
    public int BroadcastMessageId { get; set; }
    public BroadcastDraft Broadcast { get; set; }
    public int SongMessageId { get; set; }
    public long SongAlbumId { get; set; }

    // adding
    public int AddAdded { get; set; }
    public int AddSkipped { get; set; }
    public int AddRemoved { get; set; }
    public int AddPromptMessageId { get; set; }
    public int AddKeyboardMessageId { get; set; }
    public string AddPromptText { get; set; }
    public List<string> AddSeenMediaGroups { get; set; } = new();
    public Dictionary<string, int> AddReactedMediaGroups { get; set; } = new();
    public Dictionary<int, PendingMedia> AddPending { get; set; } = new();
    public List<int> AddUnsupportedHandled { get; set; } = new();
    public List<int> AddUserMessages { get; set; } = new();

    // view
    public List<int> ViewIds { get; set; } = new();
    public int ViewNavId { get; set; }
    public bool ViewEditable { get; set; }
    public string ViewLastPageKey { get; set; }
    public long ViewDeleteSeed { get; set; }
    public int ViewDeleteOffset { get; set; }
    public int ViewDeleteFlags { get; set; } = 3;
    public int ViewGoToOffset { get; set; }
    public int ViewGoToFlags { get; set; }

    // removal preview
    public List<MediaItem> RemovalPending { get; set; } = new();
    public List<long> RemovalKept { get; set; } = new();
    public long RemovalAlbumId { get; set; }
    public int RemovalPreviewOffset { get; set; }
    public int RemovalPromptMsgId { get; set; }

    public static SessionSnapshot From(UserSession s) => new()
    {
        Mode = s.Mode,
        AlbumId = s.AlbumId,
        MenuMessageId = s.MenuMessageId,
        BroadcastMessageId = s.BroadcastMessageId,
        Broadcast = s.Broadcast,
        SongMessageId = s.SongMessageId,
        SongAlbumId = s.SongAlbumId,

        AddAdded = s.Add.Added,
        AddSkipped = s.Add.Skipped,
        AddRemoved = s.Add.Removed,
        AddPromptMessageId = s.Add.PromptMessageId,
        AddKeyboardMessageId = s.Add.KeyboardMessageId,
        AddPromptText = s.Add.PromptText,
        AddSeenMediaGroups = s.Add.SeenMediaGroups.ToList(),
        AddReactedMediaGroups = new Dictionary<string, int>(s.Add.ReactedMediaGroups),
        AddPending = new Dictionary<int, PendingMedia>(s.Add.Pending),
        AddUnsupportedHandled = s.Add.UnsupportedHandled.Keys.ToList(),
        AddUserMessages = s.Add.UserMessages.Keys.ToList(),

        ViewIds = s.View.Ids.ToList(),
        ViewNavId = s.View.NavId,
        ViewEditable = s.View.Editable,
        ViewLastPageKey = s.View.LastPageKey,
        ViewDeleteSeed = s.View.DeleteSeed,
        ViewDeleteOffset = s.View.DeleteOffset,
        ViewDeleteFlags = s.View.DeleteFlags,
        ViewGoToOffset = s.View.GoToOffset,
        ViewGoToFlags = s.View.GoToFlags,

        RemovalPending = s.Removal.Pending.Select(x => x.Item).ToList(),
        RemovalKept = s.Removal.Kept.ToList(),
        RemovalAlbumId = s.Removal.AlbumId,
        RemovalPreviewOffset = s.Removal.PreviewOffset,
        RemovalPromptMsgId = s.Removal.PromptMsgId
    };

    public void ApplyTo(UserSession s)
    {
        s.Mode = Enum.IsDefined(Mode) ? Mode : Mode.Idle;
        s.AlbumId = AlbumId;
        s.MenuMessageId = MenuMessageId;
        s.BroadcastMessageId = BroadcastMessageId;
        s.Broadcast = Broadcast;
        s.SongMessageId = SongMessageId;
        s.SongAlbumId = SongAlbumId;

        s.Add.Reset();
        s.Add.Added = AddAdded;
        s.Add.Skipped = AddSkipped;
        s.Add.Removed = AddRemoved;
        s.Add.PromptMessageId = AddPromptMessageId;
        s.Add.KeyboardMessageId = AddKeyboardMessageId;
        s.Add.PromptText = AddPromptText;
        foreach (var g in AddSeenMediaGroups ?? new()) s.Add.SeenMediaGroups.Add(g);
        foreach (var (g, id) in AddReactedMediaGroups ?? new()) s.Add.ReactedMediaGroups[g] = id;
        foreach (var (id, media) in AddPending ?? new()) s.Add.Pending[id] = media;
        foreach (var id in AddUnsupportedHandled ?? new()) s.Add.UnsupportedHandled.TryAdd(id, 0);
        foreach (var id in AddUserMessages ?? new()) s.Add.UserMessages.TryAdd(id, 0);

        s.View.Ids.Clear();
        s.View.Ids.AddRange(ViewIds ?? new());
        s.View.NavId = ViewNavId;
        s.View.Editable = ViewEditable;
        s.View.LastPageKey = ViewLastPageKey;
        s.View.DeleteSeed = ViewDeleteSeed;
        s.View.DeleteOffset = ViewDeleteOffset;
        s.View.DeleteFlags = ViewDeleteFlags;
        s.View.GoToOffset = ViewGoToOffset;
        s.View.GoToFlags = ViewGoToFlags;

        s.Removal.Clear();
        s.Removal.Pending.AddRange((RemovalPending ?? new()).Select(i => (i.Number, i)));
        foreach (var id in RemovalKept ?? new()) s.Removal.Kept.Add(id);
        s.Removal.AlbumId = RemovalAlbumId;
        s.Removal.PreviewOffset = RemovalPreviewOffset;
        s.Removal.PromptMsgId = RemovalPromptMsgId;
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Null when the value is missing or unreadable (e.g. written by an incompatible version).</summary>
    public static SessionSnapshot FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<SessionSnapshot>(json);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Saved session skipped: {ex.Message}");
            return null;
        }
    }
}
