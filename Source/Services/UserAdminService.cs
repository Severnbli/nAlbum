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
using Microsoft.EntityFrameworkCore;
using nAlbum.Data;

namespace nAlbum.Services;

public sealed class UserOverview
{
    public BotUser User { get; init; }
    public int AlbumsOwned { get; init; }
    public int AlbumsJoined { get; init; }
}

/// <summary>Admin operations on users: listing, bans and removal of all of a user's data. Queries are LINQ or bound parameters.</summary>
public sealed class UserAdminService
{
    private readonly IDbContextFactory<AlbumDb> _factory;
    private readonly AlbumService _albums;
    private readonly ConcurrentDictionary<long, bool> _banned = new(); // cache of IsBanned, checked on every update

    public UserAdminService(IDbContextFactory<AlbumDb> factory, AlbumService albums)
    {
        _factory = factory;
        _albums = albums;
    }

    public async Task<bool> IsBannedAsync(long userId)
    {
        if (_banned.TryGetValue(userId, out var cached)) return cached;
        await using var db = await _factory.CreateDbContextAsync();
        var banned = await db.BotUsers.AsNoTracking().AnyAsync(u => u.UserId == userId && u.IsBanned);
        _banned[userId] = banned;
        return banned;
    }

    /// <summary>Most recently active first.</summary>
    public async Task<(List<BotUser> Items, int Total)> ListUsersAsync(int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var total = await db.BotUsers.CountAsync();
        var items = await db.BotUsers.AsNoTracking()
            .OrderByDescending(u => u.LastSeenAt).ThenBy(u => u.UserId)
            .Skip(skip).Take(take).ToListAsync();
        return (items, total);
    }

    public async Task<UserOverview> GetOverviewAsync(long userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var user = await db.BotUsers.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null) return null;
        return new UserOverview
        {
            User = user,
            AlbumsOwned = await db.Albums.CountAsync(a => a.OwnerId == userId),
            AlbumsJoined = await db.Access.CountAsync(x => x.UserId == userId)
        };
    }

    /// <summary>False when the user is unknown.</summary>
    public async Task<bool> SetBannedAsync(long userId, bool banned)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var changed = await db.BotUsers.Where(u => u.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBanned, banned));
        if (changed == 0) return false;
        _banned[userId] = banned;
        return true;
    }

    /// <summary>
    /// Removes everything stored about the user: their albums, access to other albums, view counters, language,
    /// conversations and the user record itself. A banned user keeps an empty record so the ban stays.
    /// Invite links they made for other people's albums stay valid but lose the creator.
    /// </summary>
    public async Task<(int Albums, int Media)> DeleteUserDataAsync(long userId)
    {
        int albums = 0, media = 0;
        List<long> owned;
        await using (var db = await _factory.CreateDbContextAsync())
            owned = await db.Albums.AsNoTracking().Where(a => a.OwnerId == userId).Select(a => a.Id).ToListAsync();

        foreach (var albumId in owned)
        {
            media += await _albums.DeleteAlbum(albumId);
            albums++;
        }

        await using var context = await _factory.CreateDbContextAsync();
        await using var tx = await context.Database.BeginTransactionAsync();
        await context.Access.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        await context.AlbumViewStats.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        await context.InviteLinks.Where(x => x.CreatedBy == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedBy, 0L));
        await context.UserPreferences.Where(x => x.UserId == userId).ExecuteDeleteAsync();

        var conversations = context.Conversations.Where(c => c.UserId == userId).Select(c => c.Id);
        await context.ConversationNotices.Where(n => conversations.Contains(n.ConversationId) || n.ChatId == userId).ExecuteDeleteAsync();
        await context.ConversationMessages.Where(m => conversations.Contains(m.ConversationId)).ExecuteDeleteAsync();
        await context.Conversations.Where(c => c.UserId == userId).ExecuteDeleteAsync();

        if (await context.BotUsers.AnyAsync(u => u.UserId == userId && u.IsBanned))
            await context.BotUsers.Where(u => u.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Name, (string)null));
        else
            await context.BotUsers.Where(u => u.UserId == userId).ExecuteDeleteAsync();
        await tx.CommitAsync();
        return (albums, media);
    }
}
