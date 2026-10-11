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
