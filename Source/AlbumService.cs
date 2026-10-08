using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace nAlbum.Source;

/// <summary>All database operations. Registered as a singleton; one short-lived DbContext per call.</summary>
public class AlbumService
{
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    public const int CodeLength = 10;

    private const int MaxFailedCodes = 5;
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(10);

    private readonly IDbContextFactory<AlbumDb> _factory;
    private readonly Dictionary<long, List<DateTime>> _failedCodes = new();
    private readonly object _lock = new();

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

    public bool IsThrottled(long userId)
    {
        lock (_lock)
        {
            if (!_failedCodes.TryGetValue(userId, out var list)) return false;
            list.RemoveAll(t => DateTime.UtcNow - t > FailWindow);
            return list.Count >= MaxFailedCodes;
        }
    }

    public void RegisterFailedAttempt(long userId)
    {
        lock (_lock)
        {
            if (!_failedCodes.TryGetValue(userId, out var list))
            {
                _failedCodes[userId] = list = new List<DateTime>();
            }

            list.Add(DateTime.UtcNow);
        }
    }

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

    public async Task<bool> CanView(Album album, long userId)
    {
        if (album.OwnerId == userId) return true;
        if (!album.IsOpen) return false;
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Access.AnyAsync(x => x.AlbumId == album.Id && x.UserId == userId);
    }

    public async Task<(List<AlbumListItem> Items, int Total)> ListOwned(long ownerId, int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = db.Albums.AsNoTracking().Where(a => a.OwnerId == ownerId);
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

    public async Task<(List<AlbumListItem> Items, int Total)> ListShared(long userId, int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = from x in db.Access.AsNoTracking()
                    join a in db.Albums.AsNoTracking() on x.AlbumId equals a.Id
                    where x.UserId == userId && a.IsOpen
                    select new { Album = a, x.GrantedAt };
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(r => r.GrantedAt).ThenByDescending(r => r.Album.Id)
            .Skip(skip).Take(take)
            .Select(r => new AlbumListItem
            {
                Album = r.Album,
                MediaCount = db.Media.Count(m => m.AlbumId == r.Album.Id)
            })
            .ToListAsync();
        return (items, total);
    }

    public async Task Rename(long albumId, string title)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Albums.Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Title, title));
    }

    public async Task DeleteAlbum(long albumId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Media.Where(m => m.AlbumId == albumId).ExecuteDeleteAsync();
        await db.Access.Where(x => x.AlbumId == albumId).ExecuteDeleteAsync();
        await db.Albums.Where(a => a.Id == albumId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    // -------------------------------------------- open / close / access
    /// <summary>Opens the album and issues a fresh random access code.</summary>
    public async Task<string> OpenAlbum(long albumId)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var code = GenerateCode();
            await using var db = await _factory.CreateDbContextAsync();
            var album = await db.Albums.FirstOrDefaultAsync(a => a.Id == albumId);
            if (album == null) throw new InvalidOperationException("Album not found");
            album.IsOpen = true;
            album.AccessCode = code;
            try
            {
                await db.SaveChangesAsync();
                return code;
            }
            catch (DbUpdateException)
            {
                // unique-index collision on the code (practically impossible) -> try another one
            }
        }

        throw new InvalidOperationException("Could not generate a unique access code");
    }

    /// <summary>Closes the album: the code stops working and every viewer loses access.</summary>
    public async Task CloseAlbum(long albumId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Albums.Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.IsOpen, false)
                .SetProperty(a => a.AccessCode, (string)null));
        await db.Access.Where(x => x.AlbumId == albumId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    /// <summary>Returns the album if the code is valid (and grants access), otherwise null.</summary>
    public async Task<Album> JoinByCode(long userId, string code)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var album = await db.Albums.AsNoTracking().FirstOrDefaultAsync(a => a.AccessCode == code && a.IsOpen);
        if (album == null) return null;

        if (album.OwnerId != userId &&
            !await db.Access.AnyAsync(x => x.AlbumId == album.Id && x.UserId == userId))
        {
            db.Access.Add(new AlbumAccess { AlbumId = album.Id, UserId = userId });
            await db.SaveChangesAsync();
        }

        return album;
    }

    public async Task Leave(long albumId, long userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Access.Where(x => x.AlbumId == albumId && x.UserId == userId).ExecuteDeleteAsync();
    }

    // ------------------------------------------------------------ media
    /// <summary>Returns false when the same media is already in the album.</summary>
    public async Task<bool> AddMedia(long albumId, MediaKind kind, string fileId, string fileUniqueId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (await db.Media.AnyAsync(m => m.AlbumId == albumId && m.FileUniqueId == fileUniqueId))
        {
            return false;
        }

        db.Media.Add(new MediaItem
        {
            AlbumId = albumId,
            Kind = kind,
            FileId = fileId,
            FileUniqueId = fileUniqueId
        });

        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            return false; // lost a race with an identical insert
        }
    }

    public async Task<int> MediaCount(long albumId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Media.CountAsync(m => m.AlbumId == albumId);
    }

    public async Task<List<MediaItem>> ListMedia(long albumId, int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Media.AsNoTracking().Where(m => m.AlbumId == albumId)
            .OrderBy(m => m.Id).Skip(skip).Take(take).ToListAsync();
    }
    
    /// <summary>
    /// Random order that is stable for a given seed, so Next/Prev never repeat items.
    /// (id * seed) mod a prime is a permutation of the ids, i.e. a cheap deterministic shuffle.
    /// </summary>
    public async Task<List<MediaItem>> ListMediaShuffled(long albumId, long seed, int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Media.AsNoTracking().Where(m => m.AlbumId == albumId)
            .OrderBy(m => (m.Id * seed) % 2147483647L).ThenBy(m => m.Id)
            .Skip(skip).Take(take).ToListAsync();
    }

    /// <summary>Keyset pagination: stays correct while items are being deleted.</summary>
    public async Task<List<MediaItem>> ListMediaAfter(long albumId, long afterId, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Media.AsNoTracking().Where(m => m.AlbumId == albumId && m.Id > afterId)
            .OrderBy(m => m.Id).Take(take).ToListAsync();
    }

    public async Task<MediaItem> GetMedia(long mediaId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Media.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId);
    }

    /// <summary>Resolves 1-based album positions (ordered by id) to media ids. Call BEFORE deleting.</summary>
    public async Task<List<long>> GetMediaIdsAtPositions(long albumId, IEnumerable<int> positions)
    {
        await using var db = _factory.CreateDbContext();
        var ids = new List<long>();
        foreach (var pos in positions)
        {
            var id = await db.Media.AsNoTracking().Where(m => m.AlbumId == albumId)
                .OrderBy(m => m.Id).Skip(pos - 1).Take(1)
                .Select(m => m.Id).FirstOrDefaultAsync();
            if (id != 0) ids.Add(id);
        }

        return ids;
    }

    public async Task DeleteMedia(IEnumerable<long> mediaIds)
    {
        var list = mediaIds.ToList();
        await using var db = _factory.CreateDbContext();
        await db.Media.Where(m => list.Contains(m.Id)).ExecuteDeleteAsync();
    }
}