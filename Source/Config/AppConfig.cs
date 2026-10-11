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

namespace nAlbum.Config;

public sealed class AppConfig
{
    public string BotToken { get; init; }
    public string DbPath { get; init; }
    public string LocalesDir { get; init; }
    public HashSet<long> AdminIds { get; init; } = new();

    public bool IsAdmin(long userId) => AdminIds.Contains(userId);

    public static AppConfig FromEnvironment()
    {
        var admins = new HashSet<long>();
        var raw = Environment.GetEnvironmentVariable("ADMIN_IDS") ?? "";
        foreach (var part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (long.TryParse(part, out var id)) admins.Add(id);

        return new AppConfig
        {
            BotToken = Environment.GetEnvironmentVariable("BOT_TOKEN")
                       ?? throw new InvalidOperationException("BOT_TOKEN is not set"),
            DbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "/data/albums.db",
            LocalesDir = Environment.GetEnvironmentVariable("LOCALES_DIR")
                         ?? Path.Combine(AppContext.BaseDirectory, "Locales"),
            AdminIds = admins
        };
    }
}
