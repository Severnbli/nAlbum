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

using nAlbum.Bot.Screens;
using nAlbum.Bot.Ui;
using nAlbum.Localization;
using nAlbum.Config;
using nAlbum.Services;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using TelegramBotBase.Args;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using TelegramBotBase.Interfaces;

namespace nAlbum.Bot;

/// <summary>
/// Thin router. One instance per user session. Render() stays empty on purpose.
/// The session is saved by the JSON state machine on shutdown and restored on start (see <see cref="SessionSnapshot"/>).
/// </summary>
public class AlbumsForm : FormBase, IStateForm
{
    private readonly BotContext _ctx;
    private readonly MenuScreen _menu;
    private readonly JoinScreen _join;
    private readonly AddMediaScreen _add;
    private readonly StatsScreen _stats;
    private readonly MembersScreen _members;
    private readonly AdminScreen _admin;
    private readonly SongScreen _song;
    private readonly SupportScreen _support;
    private readonly Dictionary<string, Screen> _byCallback = new();
    private readonly Dictionary<Mode, Screen> _byMode = new();
    private readonly Dictionary<string, Screen> _byCommand = new();
    private int _consumedMessageId;   // handled whole in Load; SentData must not pick it up again

    public AlbumsForm(AppConfig config, AlbumService albums, MediaService media, CodeThrottle throttle,
                      StatsService stats, UserPreferenceService preferences, LocalizationService localization,
                      UserAdminService users, SupportService support)
    {
        _ctx = new BotContext(this, config, albums, media, throttle, stats, preferences, localization, users, support);
        _menu = new MenuScreen(_ctx);
        _join = new JoinScreen(_ctx);
        _add = new AddMediaScreen(_ctx);
        _stats = new StatsScreen(_ctx);
        _members = new MembersScreen(_ctx);
        _admin = new AdminScreen(_ctx);
        _song = new SongScreen(_ctx);
        _support = new SupportScreen(_ctx);

        var screens = new Screen[]
        {
            _menu, _join, _add, new AlbumScreen(_ctx), new ViewScreen(_ctx), new RemoveScreen(_ctx), _stats, _admin, _members, _song, _support, new AdminUsersScreen(_ctx)
        };
        foreach (var s in screens)
        {
            foreach (var c in s.Callbacks) _byCallback.Add(c, s);          // Add (not indexer): duplicates must fail fast
            foreach (var m in s.TextModes) _byMode.Add(m, s);
            foreach (var c in s.Commands) _byCommand.Add(c, s);
        }
    }

    public override Task Render(MessageResult message) => Task.CompletedTask;

    public Task SaveState(SaveStateEventArgs e)
    {
        e.Values[SessionSnapshot.Key] = SessionSnapshot.From(_ctx.Session).ToJson();
        return Task.CompletedTask;
    }

    public Task LoadState(LoadStateEventArgs e)
    {
        if (e.Values != null && e.Values.TryGetValue(SessionSnapshot.Key, out var value))
            SessionSnapshot.FromJson(value?.ToString())?.ApplyTo(_ctx.Session);
        return Task.CompletedTask;
    }

    public override async Task PreLoad(MessageResult message)
    {
        if (!_ctx.IsPrivate) return;
        var from = message.UpdateData.Message?.From ?? message.UpdateData.CallbackQuery?.From;
        await _ctx.Stats.TouchUserAsync(_ctx.UserId, Text.UserName(from), Text.Username(from));
        _ctx.Banned = !_ctx.Config.IsAdmin(_ctx.UserId) && await _ctx.Users.IsBannedAsync(_ctx.UserId);
        var telegramLanguage = from?.LanguageCode;
        var preference = await _ctx.Preferences.GetLanguageAsync(_ctx.UserId);
        _ctx.Language = _ctx.Localization.Resolve(preference, telegramLanguage);
    }

    public override async Task Load(MessageResult message)
    {
        if (message.IsAction || !_ctx.IsPrivate) return;
        if (message.UpdateData.Type != UpdateType.Message) return;   // ignore edits etc.
        if (_ctx.Banned)
        {
            await _ctx.Ui.Say(_ctx.T(LocKey.Banned));
            return;
        }

        // a broadcast may itself contain or start with commands; only a bare /cancel leaves the flow
        if (_ctx.Session.Mode == Mode.Broadcast && message.MessageText?.Trim() != "/cancel")
        {
            _consumedMessageId = message.MessageId;
            await _admin.OnBroadcastMessage(message.UpdateData.Message);   // any message, text or media
            return;
        }

        if (message.IsBotCommand)
        {
            await _add.Abort();                                      // drops the add prompt and keyboard, if any
            await _song.Close();                                     // a command leaves the album
            _ctx.Session.Mode = Mode.Idle;                           // any command aborts the current flow (as before)
            var args = message.BotCommandParameters;
            if (message.BotCommand == "/start" && args.Count > 0) await _join.TryJoin(args[0]);   // deep link
            else if (message.BotCommand == "/stats" && _ctx.Config.IsAdmin(_ctx.UserId)) await _stats.ShowGlobal(message);
            else if (_byCommand.TryGetValue(message.BotCommand, out var s)) await s.OnCommand(message, message.BotCommand, args);
            else await _menu.ShowWelcome();                          // /start, /menu and anything unknown
            return;
        }

        if (_ctx.Session.Mode == Mode.InviteForward)
        {
            _consumedMessageId = message.MessageId;
            await _members.OnForward(message.UpdateData.Message);   // any forwarded message, text or media
            return;
        }

        if (message.UpdateData.Message?.ReplyToMessage != null && await _support.TryHandleReply(message.UpdateData.Message))
        {
            _consumedMessageId = message.MessageId;
            return;                                                  // a reply to a support message continues that conversation
        }

        await _add.OnIncomingMessage(message.UpdateData.Message);
        var text = message.MessageText?.Trim();
        if (string.IsNullOrEmpty(text)) return;                      // media is handled in SentData

        if (_byMode.TryGetValue(_ctx.Session.Mode, out var owner)) await owner.OnText(message, text);
    }

    public override async Task SentData(DataResult data)
    {
        // handled in Load; the flow may already be over (Mode.Idle), so the message id is checked as well
        if (!_ctx.IsPrivate || _ctx.Banned || data.Message?.MessageId == _consumedMessageId || _ctx.Session.Mode is Mode.InviteForward or Mode.Broadcast) return;
        if (_ctx.Session.Mode is Mode.Removing or Mode.RemovePrompt or Mode.SongUpload or Mode.Support or Mode.AdminReply && _byMode.TryGetValue(_ctx.Session.Mode, out var owner))
            await owner.OnMedia(data);                               // media picks items to delete, or is the album song
        else
            await _add.OnMedia(data);                                // adds in Mode.Adding, otherwise shows a hint
    }

    public override async Task Edited(MessageResult message)
    {
        if (!_ctx.IsPrivate || _ctx.Banned) return;
        await _add.OnEdited(message);
    }

    public override async Task Action(MessageResult m)
    {
        var data = m.RawData;
        if (string.IsNullOrEmpty(data) || !_ctx.IsPrivate) return;
        m.Handled = true;

        if (_ctx.Banned)
        {
            await m.ConfirmAction(_ctx.T(LocKey.Banned));
            return;
        }

        var p = data.Split(':');
        // leaving the support screens ends writing to a conversation
        if (_ctx.Session.Mode is Mode.Support or Mode.AdminReply && !_support.Callbacks.Contains(p[0]) && p[0] is not ("noop" or "pgend"))
            _ctx.Session.Mode = Mode.Idle;
        try
        {
            await _song.OnNavigate(p);                               // leaving the album removes its song
            if (_byCallback.TryGetValue(p[0], out var screen)) await screen.OnCallback(m, p);
            else await m.ConfirmAction();
        }
        catch (Exception ex) when (ex is not ApiRequestException)
        {
            await Console.Error.WriteLineAsync($"Action '{data}' failed: {ex}");
            await _ctx.Ui.Say(_ctx.T(LocKey.ErrorGeneric));
        }
    }
}
