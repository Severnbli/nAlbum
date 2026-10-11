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
