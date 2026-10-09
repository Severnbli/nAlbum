using TelegramBotBase.Base;

namespace nAlbum.Bot.Screens;

public abstract class Screen
{
    protected Screen(BotContext ctx) { Ctx = ctx; }
    protected BotContext Ctx { get; }

    /// <summary>Callback prefixes (text before the first ':') handled by this screen.</summary>
    public virtual IReadOnlyCollection<string> Callbacks => Array.Empty<string>();
    /// <summary>Session modes whose typed text this screen handles.</summary>
    public virtual IReadOnlyCollection<Mode> TextModes => Array.Empty<Mode>();
    /// <summary>Bot commands (including '/') handled by this screen.</summary>
    public virtual IReadOnlyCollection<string> Commands => Array.Empty<string>();

    public virtual Task OnCallback(MessageResult m, string[] p) => Task.CompletedTask;
    public virtual Task OnText(MessageResult m, string text) => Task.CompletedTask;
    public virtual Task OnCommand(MessageResult m, string command, IReadOnlyList<string> args) => Task.CompletedTask;
    public virtual Task OnMedia(DataResult data) => Task.CompletedTask;
}
