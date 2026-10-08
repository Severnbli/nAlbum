namespace nAlbum.Source;

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