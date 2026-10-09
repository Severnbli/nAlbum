using nAlbum.Bot.Screens;
using nAlbum.Config;
using nAlbum.Services;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using TelegramBotBase.Base;
using TelegramBotBase.Form;

namespace nAlbum.Bot;

/// <summary>Thin router. One instance per user session. Render() stays empty on purpose.</summary>
public class AlbumsForm : FormBase
{
    private readonly BotContext _ctx;
    private readonly MenuScreen _menu;
    private readonly JoinScreen _join;
    private readonly AddMediaScreen _add;
    private readonly Dictionary<string, Screen> _byCallback = new();
    private readonly Dictionary<Mode, Screen> _byMode = new();
    private readonly Dictionary<string, Screen> _byCommand = new();

    public AlbumsForm(AppConfig config, AlbumService albums, MediaService media, CodeThrottle throttle)
    {
        _ctx = new BotContext(this, config, albums, media, throttle);
        _menu = new MenuScreen(_ctx);
        _join = new JoinScreen(_ctx);
        _add = new AddMediaScreen(_ctx);

        var screens = new Screen[]
        {
            _menu, _join, _add, new AlbumScreen(_ctx), new ViewScreen(_ctx), new RemoveScreen(_ctx)
        };
        foreach (var s in screens)
        {
            foreach (var c in s.Callbacks) _byCallback.Add(c, s);          // Add (not indexer): duplicates must fail fast
            foreach (var m in s.TextModes) _byMode.Add(m, s);
            foreach (var c in s.Commands) _byCommand.Add(c, s);
        }
    }

    public override Task Render(MessageResult message) => Task.CompletedTask;

    public override async Task Load(MessageResult message)
    {
        if (message.IsAction || !_ctx.IsPrivate) return;
        if (message.UpdateData.Type != UpdateType.Message) return;   // ignore edits etc.

        if (message.IsBotCommand)
        {
            _ctx.Session.Mode = Mode.Idle;                           // any command aborts the current flow (as before)
            var args = message.BotCommandParameters;
            if (message.BotCommand == "/start" && args.Count > 0) await _join.TryJoin(args[0]);   // deep link
            else if (_byCommand.TryGetValue(message.BotCommand, out var s)) await s.OnCommand(message, message.BotCommand, args);
            else await _menu.ShowWelcome();                          // /start, /menu and anything unknown
            return;
        }

        var text = message.MessageText?.Trim();
        if (string.IsNullOrEmpty(text)) return;                      // media is handled in SentData

        if (_byMode.TryGetValue(_ctx.Session.Mode, out var owner)) await owner.OnText(message, text);
    }

    public override async Task SentData(DataResult data)
    {
        if (!_ctx.IsPrivate) return;
        await _add.OnMedia(data);                                    // adds in Mode.Adding, otherwise shows a hint
    }

    public override async Task Action(MessageResult m)
    {
        var data = m.RawData;
        if (string.IsNullOrEmpty(data) || !_ctx.IsPrivate) return;
        m.Handled = true;

        var p = data.Split(':');
        try
        {
            if (_byCallback.TryGetValue(p[0], out var screen)) await screen.OnCallback(m, p);
            else await m.ConfirmAction();
        }
        catch (Exception ex) when (ex is not ApiRequestException)
        {
            Console.Error.WriteLine($"Action '{data}' failed: {ex}");
            await _ctx.Ui.Say("⚠️ Something went wrong. Please try again.");
        }
    }
}
