using nAlbum.Bot.Ui;
using nAlbum.Config;
using nAlbum.Localization;
using nAlbum.Services;
using Telegram.Bot.Exceptions;
using TelegramBotBase.Form;
using TelegramBotBase.Interfaces;
using Telegram.Bot.Types.Enums;

namespace nAlbum.Bot;

public sealed class BotContext
{
    private readonly FormBase _form;

    public BotContext(FormBase form, AppConfig config, AlbumService albums, MediaService media,
                      CodeThrottle throttle, StatsService stats, UserPreferenceService preferences,
                                            LocalizationService localization)
                          {
                              _form = form;
                              Config = config; Albums = albums; Media = media; Throttle = throttle; Stats = stats; Preferences = preferences;
                              Localization = localization;
                              Language = LocalizationService.DefaultCode;
        Session = new UserSession();
        Ui = new BotUi(this);
        View = new MediaView(this);
        Cards = new AlbumCard(this);
    }

    public IDeviceSession Device => _form.Device;      // assigned by the framework after construction
    public long UserId => Device.DeviceId;             // private chat: chat id == user id
    public bool IsPrivate => !Device.IsGroup && !Device.IsChannel;

    public AppConfig Config { get; }
    public AlbumService Albums { get; }
    public MediaService Media { get; }
    public CodeThrottle Throttle { get; }
    public StatsService Stats { get; }
    public UserPreferenceService Preferences { get; }
    public LocalizationService Localization { get; }
    public string Language { get; set; }               // language code, e.g. "en"
    public UserSession Session { get; }
    public BotUi Ui { get; }
    public MediaView View { get; }
    public AlbumCard Cards { get; }

    public string T(string key) => Localization.T(Language, key);
    public string F(string key, params object[] args) => Localization.F(Language, key, args);

    public async Task SetAction(ChatAction action)
    {
        try
        {
            await Device.SetAction(action);
        }
        catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Could not set chat action '{action}': {ex.Message}");
        }
    }
}
