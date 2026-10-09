using nAlbum.Bot.Ui;
using nAlbum.Config;
using nAlbum.Services;
using TelegramBotBase.Form;
using TelegramBotBase.Interfaces;

namespace nAlbum.Bot;

public sealed class BotContext
{
    private readonly FormBase _form;

    public BotContext(FormBase form, AppConfig config, AlbumService albums, MediaService media,
                      CodeThrottle throttle)
    {
        _form = form;
        Config = config; Albums = albums; Media = media; Throttle = throttle;
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
    public UserSession Session { get; }
    public BotUi Ui { get; }
    public MediaView View { get; }
    public AlbumCard Cards { get; }
}
