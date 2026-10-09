using nAlbum.Source;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using TelegramBotBase.Builder;
using TelegramBotBase.Commands;

var token = Environment.GetEnvironmentVariable("BOT_TOKEN")
            ?? throw new InvalidOperationException("BOT_TOKEN is not set");
var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "/data/albums.db";

var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);

var services = new ServiceCollection()
    .AddDbContextFactory<AlbumDb>(o => o.UseSqlite($"Data Source={dbPath}"))
    .AddSingleton<AlbumService>()
    .BuildServiceProvider();

await SchemaUpgrader.RunAsync(services.GetRequiredService<IDbContextFactory<AlbumDb>>(), dbPath);

var bot = BotBaseBuilder
    .Create()
    .WithAPIKey(token)
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
        a.Add("cancel", "Cancel the current action");
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