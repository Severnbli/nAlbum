namespace nAlbum.Data;

public enum MediaKind
{
    Photo = 0,
    Video = 1
}

public class Album
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public string Title { get; set; }
    public bool IsOpen { get; set; }

    /// <summary>Random code that gives view access. Null while the album is closed.</summary>
    public string AccessCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int NextNumber { get; set; } = 1;   // next permanent media number; never decreases, never reused
}

/// <summary>A reference to media stored on Telegram's servers (file ids only, no files).</summary>
public class MediaItem
{
    public long Id { get; set; }
    public long AlbumId { get; set; }
    public MediaKind Kind { get; set; }
    public string FileId { get; set; }
    public string FileUniqueId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public int Number { get; set; }            // permanent number inside the album; gaps after deletions are normal
}

/// <summary>A user who entered a valid code of an open album.</summary>
public class AlbumAccess
{
    public long AlbumId { get; set; }
    public long UserId { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
}

public class AlbumListItem
{
    public Album Album { get; set; }
    public int MediaCount { get; set; }
}

public class BotUser
{
    public long UserId { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

/// <summary>Per album and user watch counters. Deleted with the album.</summary>
public class AlbumViewStat
{
    public long AlbumId { get; set; }
    public long UserId { get; set; }
    public int Views { get; set; }          // opened the ordered view
    public int RandomViews { get; set; }    // opened / reshuffled the random view
    public int Pages { get; set; }          // pages watched (open, Prev, Next)
    public DateTime FirstViewedAt { get; set; }
    public DateTime LastViewedAt { get; set; }
}

/// <summary>Lifetime counters that must survive deletions (key/value).</summary>
public class Counter
{
    public string Name { get; set; }
    public long Value { get; set; }
}
