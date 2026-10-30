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

namespace nAlbum.Data;

public enum MediaKind
{
    Photo = 0,
    Video = 1
}

/// <summary>Album roles in ascending order of rights. Owner is Album.OwnerId; only Member and Manager are stored in AlbumAccess.</summary>
public enum AlbumRole
{
    Member = 0,
    Manager = 1,
    Owner = 2
}

public class Album
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public string Title { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int NextNumber { get; set; } = 1;   // next permanent media number; never decreases, never reused

    /// <summary>Telegram file id of the album song (audio), or null.</summary>
    public string SongFileId { get; set; }
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

/// <summary>A member or manager of an album (joined by invite link or invited by forward).</summary>
public class AlbumAccess
{
    public long AlbumId { get; set; }
    public long UserId { get; set; }
    public AlbumRole Role { get; set; } = AlbumRole.Member;
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Invite link: makes whoever opens it a member. Null ExpiresAt / MaxUses = no limit.</summary>
public class InviteLink
{
    public long Id { get; set; }
    public long AlbumId { get; set; }
    public string Code { get; set; }
    public long CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int Uses { get; set; }

    public bool IsUsable(DateTime now) => (ExpiresAt == null || ExpiresAt > now) && (MaxUses == null || Uses < MaxUses);
}

public class AlbumListItem
{
    public Album Album { get; set; }
    public int MediaCount { get; set; }
    public AlbumRole Role { get; set; }
}

public class MemberInfo
{
    public long UserId { get; set; }
    public AlbumRole Role { get; set; }
    public string Name { get; set; }   // null when the user has not been seen with a name yet
}

public class BotUser
{
    public long UserId { get; set; }
    public string Name { get; set; }   // Telegram first and last name, refreshed while the user is active
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public bool IsBanned { get; set; }  // banned users get no answers from the bot
}

public enum ConversationStatus { Open = 0, Closed = 1 }

/// <summary>A support conversation between one user and the bot admins.</summary>
public class Conversation
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public ConversationStatus Status { get; set; } = ConversationStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool AwaitingAdmin { get; set; }   // the last message came from the user
}

public class ConversationMessage
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public bool FromAdmin { get; set; }
    public string Text { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Remembers which conversation a bot message in a chat belongs to, so a Telegram reply to it continues that conversation.</summary>
public class ConversationNotice
{
    public long ChatId { get; set; }
    public int MessageId { get; set; }
    public long ConversationId { get; set; }
}

public class ConversationListItem
{
    public Conversation Conversation { get; set; }
    public string UserName { get; set; }
    public int Messages { get; set; }
}

public class UserPreference
{
    public long UserId { get; set; }
    public string LanguageCode { get; set; }
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
