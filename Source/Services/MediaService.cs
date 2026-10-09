using Microsoft.EntityFrameworkCore;
using nAlbum.Data;

namespace nAlbum.Services;

/// <summary>Media add/list/count/resolve/delete. Registered as a singleton; one short-lived DbContext per call.</summary>
public class MediaService
{
    private readonly IDbContextFactory<AlbumDb> _factory;
    private readonly StatsService _stats;
    private readonly SemaphoreSlim _addLock = new(1, 1);

    public MediaService(IDbContextFactory<AlbumDb> factory, StatsService stats)
    {
        _factory = factory;
        _stats = stats;
    }

    /// <summary>
    /// Returns false when the same media is already in the album.
    /// Numbers are allocated under an in-process lock: media-group items arrive concurrently (UseThreadPool) and SQLite
    /// would otherwise fail one of two read-modify-write transactions. The app is single-process, so this is sufficient.
    /// </summary>
    public async Task<bool> AddMedia(long albumId, MediaKind kind, string fileId, string fileUniqueId)
    {
        await _addLock.WaitAsync();
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            if (await db.Media.AnyAsync(m => m.AlbumId == albumId && m.FileUniqueId == fileUniqueId)) return false;

            await using var tx = await db.Database.BeginTransactionAsync();
            var album = await db.Albums.FirstOrDefaultAsync(a => a.Id == albumId); // tracked
            if (album == null) return false;

            var number = album.NextNumber;
            album.NextNumber = number + 1;
            db.Media.Add(new MediaItem
            {
                AlbumId = albumId, Kind = kind, FileId = fileId, FileUniqueId = fileUniqueId, Number = number
            });

            await db.SaveChangesAsync();
            await tx.CommitAsync();
            await _stats.IncrementAsync(StatKeys.MediaAdded);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
        finally
        {
            _addLock.Release();
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

    /// <summary>Resolves permanent numbers to items of this album. Deleted or unused numbers are absent.</summary>
    public async Task<List<(int Number, MediaItem Item)>> ResolveNumbers(long albumId, IEnumerable<int> numbers)
    {
        var wanted = numbers.Where(n => n >= 1).Distinct().ToList();
        if (wanted.Count == 0) return new List<(int, MediaItem)>();

        await using var db = await _factory.CreateDbContextAsync();
        var items = await db.Media.AsNoTracking()
            .Where(m => m.AlbumId == albumId && wanted.Contains(m.Number))
            .OrderBy(m => m.Number).ToListAsync();
        return items.Select(m => (m.Number, m)).ToList();
    }

    /// <summary>Deletes the given items, but only if they belong to the album. Returns how many rows were deleted.</summary>
    public async Task<int> DeleteMediaInAlbum(long albumId, IEnumerable<long> mediaIds)
    {
        var list = mediaIds.ToList();
        await using var db = await _factory.CreateDbContextAsync();
        var deleted = await db.Media.Where(m => m.AlbumId == albumId && list.Contains(m.Id)).ExecuteDeleteAsync();
        await _stats.IncrementAsync(StatKeys.MediaDeleted, deleted);
        return deleted;
    }
}
