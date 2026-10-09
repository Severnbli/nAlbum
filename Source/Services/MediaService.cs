using Microsoft.EntityFrameworkCore;
using nAlbum.Data;

namespace nAlbum.Services;

/// <summary>Media add/list/count/resolve/delete. Registered as a singleton; one short-lived DbContext per call.</summary>
public class MediaService
{
    private readonly IDbContextFactory<AlbumDb> _factory;
    private readonly SemaphoreSlim _addLock = new(1, 1);

    public MediaService(IDbContextFactory<AlbumDb> factory)
    {
        _factory = factory;
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

    /// <summary>1-based position of each media item in its album (ordered by Id): the "real" numbers shown as #n.</summary>
    public async Task<Dictionary<long, int>> GetPositions(long albumId, IEnumerable<long> mediaIds)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var result = new Dictionary<long, int>();
        foreach (var id in mediaIds)
        {
            result[id] = await db.Media.CountAsync(m => m.AlbumId == albumId && m.Id <= id);
        }

        return result;
    }

    /// <summary>
    /// Resolves 1-based album positions (album order = ascending Id) to (position, item). Positions beyond the album size are dropped.
    /// </summary>
    public async Task<List<(int Position, MediaItem Item)>> ResolvePositions(long albumId, IEnumerable<int> positions)
    {
        var wanted = positions.Where(p => p >= 1).Distinct().OrderBy(p => p).ToList();
        if (wanted.Count == 0) return new List<(int, MediaItem)>();

        await using var db = await _factory.CreateDbContextAsync();
        var ids = await db.Media.AsNoTracking().Where(m => m.AlbumId == albumId)
            .OrderBy(m => m.Id).Take(wanted[^1]).Select(m => m.Id).ToListAsync(); // ids[k-1] = item at position k

        var picked = wanted.Where(p => p <= ids.Count).Select(p => (Position: p, Id: ids[p - 1])).ToList();
        var idList = picked.Select(x => x.Id).ToList();
        var byId = (await db.Media.AsNoTracking().Where(m => m.AlbumId == albumId && idList.Contains(m.Id)).ToListAsync())
            .ToDictionary(m => m.Id);

        return picked.Where(x => byId.ContainsKey(x.Id)).Select(x => (x.Position, byId[x.Id])).ToList();
    }

    /// <summary>Deletes the given items, but only if they belong to the album. Returns how many rows were deleted.</summary>
    public async Task<int> DeleteMediaInAlbum(long albumId, IEnumerable<long> mediaIds)
    {
        var list = mediaIds.ToList();
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Media.Where(m => m.AlbumId == albumId && list.Contains(m.Id)).ExecuteDeleteAsync();
    }

    public async Task DeleteMedia(IEnumerable<long> mediaIds)
    {
        var list = mediaIds.ToList();
        await using var db = _factory.CreateDbContext();
        await db.Media.Where(m => list.Contains(m.Id)).ExecuteDeleteAsync();
    }
}
