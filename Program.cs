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

using nAlbum.Bot;
using nAlbum.Config;
using nAlbum.Data;
using nAlbum.Localization;
using nAlbum.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using TelegramBotBase.Builder;
using TelegramBotBase.Commands;

var config = AppConfig.FromEnvironment();

var dbDir = Path.GetDirectoryName(Path.GetFullPath(config.DbPath));
if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);

var services = new ServiceCollection()
    .AddSingleton(config)
    .AddDbContextFactory<AlbumDb>(o => o.UseSqlite($"Data Source={config.DbPath}"))
    .AddSingleton<AlbumService>()
    .AddSingleton<StatsService>()
    .AddSingleton<MediaService>()
    .AddSingleton<CodeThrottle>()
    .AddSingleton<UserPreferenceService>()
    .AddSingleton(LocalizationService.Load(config.LocalesDir))
    .BuildServiceProvider();

await SchemaUpgrader.RunAsync(services.GetRequiredService<IDbContextFactory<AlbumDb>>(), config.DbPath);

var bot = BotBaseBuilder
    .Create()
    .WithAPIKey(config.BotToken)
    .DefaultMessageLoop()
    .WithServiceProvider<AlbumsForm>(services) // forms are created through DI
    .NoProxy()
    .UseDefaultRequestDispatcher()
    .CustomCommands(a =>
    {
        a.Start("Main menu");
        a.Add("menu", "Main menu");
        a.Add("new", "Create an album");
        a.Add("join", "Open an album by code");
        a.Add("albums", "Albums");
        a.Add("cancel", "Cancel the current action");
        a.Add("language", "Choose bot language");
        a.Add("help", "Help");
    })
    .NoSerialization()   // form state is transient; albums live in SQLite
    .DefaultLanguage()
    .UseThreadPool()   // keeps media in upload order
    .Build();

BotInfo.Username = (await bot.Client.TelegramClient.GetMe()).Username;

await bot.UploadBotCommands();
await bot.Start();
Console.WriteLine($"Bot @{BotInfo.Username} started.");
await Task.Delay(Timeout.Infinite);
