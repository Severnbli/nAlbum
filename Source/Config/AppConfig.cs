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
