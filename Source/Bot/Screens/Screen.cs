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
