using Microsoft.EntityFrameworkCore;
using nAlbum.Data;

namespace nAlbum.Services;

public sealed class UserPreferenceService
{
    private readonly IDbContextFactory<AlbumDb> _factory;

    public UserPreferenceService(IDbContextFactory<AlbumDb> factory) { _factory = factory; }

    public async Task<string> GetLanguageAsync(long userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.UserPreferences.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.LanguageCode)
            .FirstOrDefaultAsync();
    }

    public async Task SetLanguageAsync(long userId, string languageCode)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO UserPreferences (UserId, LanguageCode) VALUES ({userId}, {languageCode})
            ON CONFLICT(UserId) DO UPDATE SET LanguageCode = excluded.LanguageCode");
    }
}
