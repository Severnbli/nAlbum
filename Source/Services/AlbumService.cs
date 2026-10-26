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

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using nAlbum.Data;

namespace nAlbum.Services;

/// <summary>Album and access operations. Registered as a singleton; one short-lived DbContext per call.</summary>
public class AlbumService
{
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    public const int CodeLength = 10;

    private readonly IDbContextFactory<AlbumDb> _factory;

    public AlbumService(IDbContextFactory<AlbumDb> factory)
    {
        _factory = factory;
    }

    // ------------------------------------------------------------ codes
    public static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(chars);
    }

    public static bool LooksLikeCode(string s) =>
        s != null && s.Length == CodeLength && s.All(c => CodeAlphabet.Contains(c));

    // ----------------------------------------------------------- albums
    public async Task<Album> CreateAlbum(long ownerId, string title)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var album = new Album { OwnerId = ownerId, Title = title };
        db.Albums.Add(album);
        await db.SaveChangesAsync();
        return album;
    }

    public async Task<Album> GetAlbum(long id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Albums.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
    }

    /// <summary>The user's role in the album, or null without access.</summary>
    public async Task<AlbumRole?> GetRole(Album album, long userId)
    {
        if (album.OwnerId == userId) return AlbumRole.Owner;
        await using var db = await _factory.CreateDbContextAsync();
        var access = await db.Access.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AlbumId == album.Id && x.UserId == userId);
        return access?.Role;
    }

    public async Task<bool> CanView(Album album, long userId) => await GetRole(album, userId) != null;

    public async Task<bool> CanManage(Album album, long userId) => await GetRole(album, userId) >= AlbumRole.Manager;

    /// <summary>Albums the user owns or has access to, newest first, with the user's role in each.</summary>
    public async Task<(List<AlbumListItem> Items, int Total)> ListAccessible(long userId, int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = db.Albums.AsNoTracking()
            .Where(a => a.OwnerId == userId || db.Access.Any(x => x.AlbumId == a.Id && x.UserId == userId));
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.Id)
            .Skip(skip).Take(take)
            .Select(a => new AlbumListItem
            {
                Album = a,
                MediaCount = db.Media.Count(m => m.AlbumId == a.Id),
                Role = a.OwnerId == userId
                    ? AlbumRole.Owner
                    : db.Access.Where(x => x.AlbumId == a.Id && x.UserId == userId).Select(x => x.Role).First()
            })
            .ToListAsync();
        return (items, total);
    }

    public async Task<(List<AlbumListItem> Items, int Total)> ListAll(int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = db.Albums.AsNoTracking();
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.Id)
            .Skip(skip).Take(take)
            .Select(a => new AlbumListItem
            {
                Album = a,
                MediaCount = db.Media.Count(m => m.AlbumId == a.Id)
            })
            .ToListAsync();
        return (items, total);
    }

    /// <summary>Users with access, not counting the owner.</summary>
    public async Task<int> MemberCount(long albumId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Access.CountAsync(x => x.AlbumId == albumId);
    }

    public async Task Rename(long albumId, string title)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Albums.Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Title, title));
    }

    /// <summary>Sets (or with null removes) the album song.</summary>
    public async Task SetSong(long albumId, string fileId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Albums.Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.SongFileId, fileId));
    }

    public async Task<int> DeleteAlbum(long albumId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var mediaRemoved = await db.Media.Where(m => m.AlbumId == albumId).ExecuteDeleteAsync();
        await db.Access.Where(x => x.AlbumId == albumId).ExecuteDeleteAsync();
        await db.InviteLinks.Where(x => x.AlbumId == albumId).ExecuteDeleteAsync();
        await db.AlbumViewStats.Where(x => x.AlbumId == albumId).ExecuteDeleteAsync();
        await db.Albums.Where(a => a.Id == albumId).ExecuteDeleteAsync();
        await tx.CommitAsync();
        return mediaRemoved;
    }

    // ---------------------------------------------------------- members
    /// <summary>Owner first, then managers, then members (oldest first).</summary>
    public async Task<List<MemberInfo>> ListMembers(Album album)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var members = await (from x in db.Access.AsNoTracking()
                             where x.AlbumId == album.Id
                             join u in db.BotUsers.AsNoTracking() on x.UserId equals u.UserId into users
                             from u in users.DefaultIfEmpty()
                             orderby x.Role descending, x.GrantedAt, x.UserId
                             select new MemberInfo { UserId = x.UserId, Role = x.Role, Name = u.Name })
            .ToListAsync();
        var ownerName = await db.BotUsers.AsNoTracking()
            .Where(u => u.UserId == album.OwnerId).Select(u => u.Name).FirstOrDefaultAsync();
        members.Insert(0, new MemberInfo { UserId = album.OwnerId, Role = AlbumRole.Owner, Name = ownerName });
        return members;
    }

    /// <summary>Adds the user as a member. False when they are the owner or already have access.</summary>
    public async Task<bool> AddMember(long albumId, long userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (await db.Albums.AnyAsync(a => a.Id == albumId && a.OwnerId == userId)) return false;
        if (await db.Access.AnyAsync(x => x.AlbumId == albumId && x.UserId == userId)) return false;
        db.Access.Add(new AlbumAccess { AlbumId = albumId, UserId = userId });
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            return false; // added concurrently
        }
    }

    /// <summary>Removes a member or manager (kick or leave). The owner has no access row and is never affected.</summary>
    public async Task Leave(long albumId, long userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Access.Where(x => x.AlbumId == albumId && x.UserId == userId).ExecuteDeleteAsync();
    }

    /// <summary>Promotes or demotes an existing member. Returns false when the user has no access row.</summary>
    public async Task<bool> SetRole(long albumId, long userId, AlbumRole role)
    {
        if (role == AlbumRole.Owner) throw new ArgumentOutOfRangeException(nameof(role), "Use TransferOwnership");
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Access.Where(x => x.AlbumId == albumId && x.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Role, role)) > 0;
    }

    /// <summary>Makes a member or manager the owner; the previous owner becomes a manager.</summary>
    public async Task<bool> TransferOwnership(long albumId, long currentOwnerId, long newOwnerId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var album = await db.Albums.FirstOrDefaultAsync(a => a.Id == albumId && a.OwnerId == currentOwnerId);
        var access = await db.Access.FirstOrDefaultAsync(x => x.AlbumId == albumId && x.UserId == newOwnerId);
        if (album == null || access == null) return false;

        db.Access.Remove(access);
        db.Access.Add(new AlbumAccess { AlbumId = albumId, UserId = currentOwnerId, Role = AlbumRole.Manager });
        album.OwnerId = newOwnerId;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    // ----------------------------------------------------- invite links
    public async Task<InviteLink> CreateInvite(long albumId, long createdBy, TimeSpan? lifetime, int? maxUses)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var now = DateTime.UtcNow;
            var link = new InviteLink
            {
                AlbumId = albumId, Code = GenerateCode(), CreatedBy = createdBy, CreatedAt = now,
                ExpiresAt = lifetime == null ? null : now + lifetime.Value, MaxUses = maxUses
            };
            db.InviteLinks.Add(link);
            try
            {
                await db.SaveChangesAsync();
                return link;
            }
            catch (DbUpdateException)
            {
                // unique-index collision on the code (practically impossible) -> try another one
            }
        }

        throw new InvalidOperationException("Could not generate a unique invite code");
    }

    public async Task<(List<InviteLink> Items, int Total)> ListInvites(long albumId, int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = db.InviteLinks.AsNoTracking().Where(x => x.AlbumId == albumId);
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(x => x.Id).Skip(skip).Take(take).ToListAsync();
        return (items, total);
    }

    /// <summary>Deletes the link, but only if it belongs to the album.</summary>
    public async Task<bool> DeleteInvite(long albumId, long inviteId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.InviteLinks.Where(x => x.Id == inviteId && x.AlbumId == albumId).ExecuteDeleteAsync() > 0;
    }

    /// <summary>
    /// Joins by an invite code. Owners and existing members get the album without using the link up;
    /// otherwise one use is taken atomically, so a limited link never admits more people than allowed.
    /// </summary>
    public async Task<(Album Album, bool Joined)> JoinByCode(long userId, string code)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var link = await db.InviteLinks.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code);
        if (link == null) return (null, false);
        var album = await db.Albums.AsNoTracking().FirstOrDefaultAsync(a => a.Id == link.AlbumId);
        if (album == null) return (null, false);

        if (album.OwnerId == userId || await db.Access.AnyAsync(x => x.AlbumId == album.Id && x.UserId == userId))
            return (album, false);
        if (!link.IsUsable(now)) return (null, false);

        await using var tx = await db.Database.BeginTransactionAsync();
        var taken = await db.InviteLinks
            .Where(x => x.Id == link.Id && (x.ExpiresAt == null || x.ExpiresAt > now) && (x.MaxUses == null || x.Uses < x.MaxUses))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Uses, x => x.Uses + 1));
        if (taken == 0) return (null, false);
        db.Access.Add(new AlbumAccess { AlbumId = album.Id, UserId = userId });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return (album, true);
    }
}
