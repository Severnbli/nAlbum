using System.Collections.Concurrent;
using nAlbum.Data;

namespace nAlbum.Bot;

public enum Mode { Idle, NewTitle, Rename, Adding, Removing, RemovePrompt, GoToNumber }

public sealed class UserSession
{
    public Mode Mode { get; set; } = Mode.Idle;
    public long AlbumId { get; set; }                 // was _albumId
    public AddState Add { get; } = new();
    public ViewState View { get; } = new();
    public RemovalState Removal { get; } = new();
}

public sealed class AddState
{
    public int Added;                 // was _added
    public int Skipped;               // was _skipped
    public HashSet<string> SeenMediaGroups { get; } = new();
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
        Pending.Clear();
        UnsupportedHandled.Clear();
        UserMessages.Clear();
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
    public int GoToOffset;                   // page to return to when "Go to number" is cancelled
    public int GoToFlags;                    // numbers flags to keep after jumping
}

public sealed class RemovalState
{
    public List<(int Number, MediaItem Item)> Pending { get; } = new(); // was _pending
    public HashSet<long> Kept { get; } = new();                          // was _pendingKept
    public long AlbumId;                                                 // was _pendingAlbumId
    public int PreviewOffset;                                            // was _previewOffset
    public int PromptMsgId;                                              // was _promptMsgId

    public void Clear() { Pending.Clear(); Kept.Clear(); AlbumId = 0; PreviewOffset = 0; PromptMsgId = 0; } // was ClearPending()
}
