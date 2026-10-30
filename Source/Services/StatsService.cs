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

public static class StatKeys
{
    public const string AlbumsCreated = "albums_created";
    public const string AlbumsDeleted = "albums_deleted";
    public const string InvitesCreated = "invites_created";
    public const string ForwardInvites = "forward_invites";
    public const string MediaAdded = "media_added";
    public const string MediaDeleted = "media_deleted";
    public const string ViewsTotal = "views_total";
    public const string RandomViewsTotal = "random_views_total";
    public const string PagesTotal = "pages_total";
    public const string CodeJoins = "code_joins";
    public const string CodeFailures = "code_failures";
    public const string CodeThrottled = "code_throttled";
}

public sealed class PublicStats
{
    public int Users, Albums, Media;
    public long Views, Pages;
}

public sealed class AlbumWatch
{
    public long Views;
    public long Pages;
}

public sealed class TopAlbum
{
    public long AlbumId;
    public string Title;
    public long Views;
    public long Pages;
}

public sealed class GlobalStats
{
    public int Users, ActiveUsers24h, ActiveUsers7d, NewUsers7d;
    public int Albums, SharedAlbums, AlbumsCreated7d, Memberships, Managers;
    public int Media, Photos, Videos, MediaAdded7d, LargestAlbum;
    public double AvgMediaPerAlbum;
    public long ViewsTotal, RandomViewsTotal, PagesTotal;
    public long AlbumsCreatedTotal, AlbumsDeletedTotal, MediaAddedTotal, MediaDeletedTotal;
    public long CodeJoins, CodeFailures, CodeThrottled, InvitesCreatedTotal;
    public List<TopAlbum> Top { get; set; } = new();
    public DateTime Now;
}

/// <summary>Writes fail independently so optional statistics never interrupt bot flows.</summary>
public sealed class StatsService
{
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<AlbumDb> _factory;
    private readonly ConcurrentDictionary<long, DateTime> _lastTouch = new();

    public StatsService(IDbContextFactory<AlbumDb> factory) { _factory = factory; }

    public async Task IncrementAsync(string key, long delta = 1)
    {
        if (delta == 0) return;
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Counters (Name, Value) VALUES ({key}, {delta}) ON CONFLICT(Name) DO UPDATE SET Value = Value + {delta}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"Stats: increment '{key}' failed: {ex.Message}"); }
    }

    /// <summary>Marks the user as seen and stores their display name. Writes at most once per five minutes per user.</summary>
    public async Task TouchUserAsync(long userId, string name, string username = null)
    {
        var now = DateTime.UtcNow;
        while (true)
        {
            if (_lastTouch.TryGetValue(userId, out var last))
            {
                if (now - last < TouchEvery) return;
                if (!_lastTouch.TryUpdate(userId, now, last)) continue;
            }
            else if (!_lastTouch.TryAdd(userId, now))
            {
                continue;
            }

            break;
        }

        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO BotUsers (UserId, Name, Username, FirstSeenAt, LastSeenAt) VALUES ({userId}, {name}, {username}, {now}, {now}) ON CONFLICT(UserId) DO UPDATE SET LastSeenAt = {now}, Name = COALESCE({name}, Name), Username = {username}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"Stats: touch user failed: {ex.Message}"); }
    }

    /// <summary>Updates the name of a known user (e.g. seen in a forwarded message). Never adds a user.</summary>
    public async Task RefreshNameAsync(long userId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            await db.BotUsers.Where(u => u.UserId == userId).ExecuteUpdateAsync(x => x.SetProperty(u => u.Name, name));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Stats: refresh name failed: {ex.Message}"); }
    }

    /// <summary>Everyone who has used the bot (broadcast recipients).</summary>
    public async Task<List<long>> ListUserIdsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.BotUsers.AsNoTracking().OrderBy(u => u.UserId).Select(u => u.UserId).ToListAsync();
    }

    /// <summary>Records an ordered or random view opening.</summary>
    public async Task RecordViewAsync(long albumId, long userId, bool random)
    {
        var now = DateTime.UtcNow;
        var ordered = random ? 0 : 1;
        var shuffled = random ? 1 : 0;
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO AlbumViewStats (AlbumId, UserId, Views, RandomViews, Pages, FirstViewedAt, LastViewedAt)
                VALUES ({albumId}, {userId}, {ordered}, {shuffled}, 0, {now}, {now})
                ON CONFLICT(AlbumId, UserId) DO UPDATE SET Views = Views + {ordered}, RandomViews = RandomViews + {shuffled}, LastViewedAt = {now}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"Stats: record view failed: {ex.Message}"); return; }

        await IncrementAsync(random ? StatKeys.RandomViewsTotal : StatKeys.ViewsTotal);
    }

    /// <summary>Records a newly displayed page (open, Previous, or Next).</summary>
    public async Task RecordPageAsync(long albumId, long userId)
    {
        var now = DateTime.UtcNow;
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO AlbumViewStats (AlbumId, UserId, Views, RandomViews, Pages, FirstViewedAt, LastViewedAt)
                VALUES ({albumId}, {userId}, 0, 0, 1, {now}, {now})
                ON CONFLICT(AlbumId, UserId) DO UPDATE SET Pages = Pages + 1, LastViewedAt = {now}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"Stats: record page failed: {ex.Message}"); return; }

        await IncrementAsync(StatKeys.PagesTotal);
    }

    /// <summary>Returns public menu totals, or null when inline statistics are unavailable.</summary>
    public async Task<PublicStats> GetPublicStatsAsync()
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            var keys = new[] { StatKeys.ViewsTotal, StatKeys.RandomViewsTotal, StatKeys.PagesTotal };
            var counters = await db.Counters.AsNoTracking()
                .Where(x => keys.Contains(x.Name)).ToDictionaryAsync(x => x.Name, x => x.Value);
            long Count(string key) => counters.TryGetValue(key, out var value) ? value : 0;

            return new PublicStats
            {
                Users = await db.BotUsers.CountAsync(),
                Albums = await db.Albums.CountAsync(),
                Media = await db.Media.CountAsync(),
                Views = Count(StatKeys.ViewsTotal) + Count(StatKeys.RandomViewsTotal),
                Pages = Count(StatKeys.PagesTotal)
            };
        }
        catch (Exception ex)
        {
           await Console.Error.WriteLineAsync($"Stats: public stats failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Returns per-album totals, or null when inline statistics are unavailable.</summary>
    public async Task<AlbumWatch> GetAlbumWatchAsync(long albumId)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            var rows = db.AlbumViewStats.AsNoTracking().Where(x => x.AlbumId == albumId);
            var views = await rows.SumAsync(x => (long)(x.Views + x.RandomViews));
            var pages = await rows.SumAsync(x => (long)x.Pages);
            return new AlbumWatch { Views = views, Pages = pages };
        }
        catch (Exception ex)
        {
           await Console.Error.WriteLineAsync($"Stats: album watch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Admin-only aggregate report. Errors are surfaced to the screen caller.</summary>
    public async Task<GlobalStats> GetGlobalStatsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var dayAgo = now.AddDays(-1);
        var weekAgo = now.AddDays(-7);
        var counters = await db.Counters.AsNoTracking().ToDictionaryAsync(c => c.Name, c => c.Value);
        long Count(string key) => counters.TryGetValue(key, out var value) ? value : 0;

        var s = new GlobalStats { Now = now };
        s.Users = await db.BotUsers.CountAsync();
        s.ActiveUsers24h = await db.BotUsers.CountAsync(u => u.LastSeenAt >= dayAgo);
        s.ActiveUsers7d = await db.BotUsers.CountAsync(u => u.LastSeenAt >= weekAgo);
        s.NewUsers7d = await db.BotUsers.CountAsync(u => u.FirstSeenAt >= weekAgo);
        s.Albums = await db.Albums.CountAsync();
        s.SharedAlbums = await db.Albums.CountAsync(a => db.Access.Any(x => x.AlbumId == a.Id));
        s.AlbumsCreated7d = await db.Albums.CountAsync(a => a.CreatedAt >= weekAgo);
        s.Memberships = await db.Access.CountAsync();
        s.Managers = await db.Access.CountAsync(x => x.Role == AlbumRole.Manager);
        s.Media = await db.Media.CountAsync();
        s.Photos = await db.Media.CountAsync(m => m.Kind == MediaKind.Photo);
        s.Videos = await db.Media.CountAsync(m => m.Kind == MediaKind.Video);
        s.MediaAdded7d = await db.Media.CountAsync(m => m.AddedAt >= weekAgo);
        s.LargestAlbum = await db.Media.GroupBy(m => m.AlbumId)
            .Select(g => g.Count()).OrderByDescending(count => count).FirstOrDefaultAsync();
        s.AvgMediaPerAlbum = s.Albums == 0 ? 0 : (double)s.Media / s.Albums;

        s.ViewsTotal = Count(StatKeys.ViewsTotal);
        s.RandomViewsTotal = Count(StatKeys.RandomViewsTotal);
        s.PagesTotal = Count(StatKeys.PagesTotal);
        s.AlbumsCreatedTotal = Count(StatKeys.AlbumsCreated);
        s.AlbumsDeletedTotal = Count(StatKeys.AlbumsDeleted);
        s.MediaAddedTotal = Count(StatKeys.MediaAdded);
        s.MediaDeletedTotal = Count(StatKeys.MediaDeleted);
        s.CodeJoins = Count(StatKeys.CodeJoins);
        s.CodeFailures = Count(StatKeys.CodeFailures);
        s.CodeThrottled = Count(StatKeys.CodeThrottled);
        s.InvitesCreatedTotal = Count(StatKeys.InvitesCreated);

        var top = await db.AlbumViewStats.AsNoTracking()
            .GroupBy(x => x.AlbumId)
            .Select(g => new
            {
                AlbumId = g.Key,
                Views = g.Sum(x => x.Views + x.RandomViews),
                Pages = g.Sum(x => x.Pages)
            })
            .OrderByDescending(x => x.Views).ThenByDescending(x => x.Pages)
            .Take(5).ToListAsync();
        var ids = top.Select(t => t.AlbumId).ToList();
        var titles = await db.Albums.AsNoTracking().Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Title);
        s.Top = top.Select(t => new TopAlbum
        {
            AlbumId = t.AlbumId,
            Title = titles.GetValueOrDefault(t.AlbumId, "?"),
            Views = t.Views,
            Pages = t.Pages
        }).ToList();

        return s;
    }
}
