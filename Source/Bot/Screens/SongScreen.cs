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
using nAlbum.Data;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using TelegramBotBase.Sessions;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

/// <summary>
/// Album song: managers set it (an audio file id); anyone with access plays it. The song message stays in the chat
/// while the user works with that album, so listening is not interrupted by menu changes: Done only removes its
/// button and brings the album card back. Leaving the album (another screen, another album, a command) deletes it.
/// </summary>
public sealed class SongScreen : Screen
{
    /// <summary>Callbacks that stay inside an album (album id in p[1]); the song survives them for its own album.</summary>
    private static readonly HashSet<string> AlbumCallbacks = new()
    {
        "al", "aln", "mg", "view", "vp", "rnd", "rp", "gp", "add", "done", "rmlist", "rmp", "rmy", "rmc", "ren", "del",
        "mem", "mu", "mk", "mkok", "mpr", "mde", "mxf", "mto", "mtook", "inv", "invn", "inve", "invc", "invxy", "invuy", "invd", "invf",
        "song", "songok", "sng", "sngdel"
    };

    /// <summary>Callbacks without an album id that never leave the current screen.</summary>
    private static readonly HashSet<string> NeutralCallbacks = new() { "rmt", "noop", "pgend" };

    public SongScreen(BotContext ctx) : base(ctx) { }

    /// <summary>Deletes the song message when a callback leaves its album.</summary>
    public async Task OnNavigate(string[] p)
    {
        var s = Ctx.Session;
        if (s.SongMessageId == 0 || NeutralCallbacks.Contains(p[0])) return;
        if (AlbumCallbacks.Contains(p[0]) && Text.Long(p, 1) == s.SongAlbumId) return;
        await Close();
    }

    /// <summary>Deletes the song message, if any.</summary>
    public async Task Close()
    {
        var s = Ctx.Session;
        if (s.SongMessageId == 0) return;
        var id = s.SongMessageId;
        s.SongMessageId = 0;
        s.SongAlbumId = 0;
        await Ctx.Ui.TryDelete(id);
    }

    public override IReadOnlyCollection<string> Callbacks => new[] { "song", "songok", "sng", "sngdel" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.SongUpload };

    public override async Task OnText(MessageResult m, string text)
    {
        var album = await UploadAlbum();
        if (album == null) return;
        await Ctx.Ui.TryDelete(m.MessageId);
        await Ctx.Ui.ShowMenu(Ctx.T(LocKey.SongOnlyAudio) + "\n\n" + Prompt(album), PromptButtons(album));
    }

    /// <summary>Media in Mode.SongUpload: an audio file becomes the album song.</summary>
    public override async Task OnMedia(DataResult data)
    {
        var album = await UploadAlbum();
        if (album == null) return;
        var msg = data.Message;
        await Ctx.Ui.TryDelete(msg.MessageId);
        if (msg.Audio == null)
        {
            await Ctx.Ui.ShowMenu(Ctx.T(LocKey.SongOnlyAudio) + "\n\n" + Prompt(album), PromptButtons(album));
            return;
        }

        Ctx.Session.Mode = Mode.Idle;
        await Ctx.Albums.SetSong(album.Id, msg.Audio.FileId);
        album.SongFileId = msg.Audio.FileId;
        var (text, bf) = await Ctx.Cards.BuildManage(album);
        await Ctx.Ui.ShowMenu(Ctx.T(LocKey.SongSaved) + "\n\n" + text, bf);
    }

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        switch (p[0])
        {
            case "sng":
            {
                var album = await Ctx.Ui.ManagedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                Ctx.Session.Mode = Mode.SongUpload;
                Ctx.Session.AlbumId = album.Id;
                await Ctx.Ui.Show(m, Prompt(album), PromptButtons(album));
                break;
            }

            case "sngdel":
            {
                var album = await Ctx.Ui.ManagedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction(Ctx.T(LocKey.SongRemoved));
                Ctx.Session.Mode = Mode.Idle;
                await Ctx.Albums.SetSong(album.Id, null);
                album.SongFileId = null;
                var (text, bf) = await Ctx.Cards.BuildManage(album);
                await Ctx.Ui.Show(m, text, bf);
                break;
            }

            case "song":
            {
                var album = await Ctx.Ui.ViewableAlbum(m, p);
                if (album == null) return;
                if (album.SongFileId == null)
                {
                    await m.ConfirmAction(Ctx.T(LocKey.SongNone));
                    return;
                }

                await m.ConfirmAction();
                await Close(); // never two songs on screen
                var bf = new ButtonForm();
                bf.AddButtonRow(Ctx.T(LocKey.ButtonDone), $"songok:{album.Id}");
                try
                {
                    await Ctx.SetAction(ChatAction.UploadDocument);
                    var sent = await Ctx.Device.Dispatch(a => a.SendAudio(Ctx.Device.DeviceId, InputFile.FromFileId(album.SongFileId),
                        caption: Ctx.F(LocKey.SongCaption, Text.H(album.Title)), parseMode: ParseMode.Html,
                        replyMarkup: new InlineKeyboardMarkup(bf.ToInlineButtonArray())));
                    Ctx.Session.SongMessageId = sent.MessageId;
                    Ctx.Session.SongAlbumId = album.Id;
                }
                catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
                {
                    await Console.Error.WriteLineAsync($"Song of album {album.Id} could not be sent: {ex.Message}");
                    await Ctx.Ui.Say(Ctx.T(LocKey.SongFailed));
                    return;
                }

                await Ctx.Ui.TryDelete(m.MessageId); // the card comes back under the song on Done
                break;
            }

            case "songok":
            {
                var album = await Ctx.Ui.ViewableAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                try
                {
                    await Ctx.Device.Dispatch(a => a.EditMessageReplyMarkup(Ctx.Device.DeviceId, m.MessageId, null));
                }
                catch (ApiRequestException ex)
                {
                    await Console.Error.WriteLineAsync($"Could not remove the song button: {ex.Message}");
                }

                var (card, cardButtons) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Say(card, cardButtons);
                break;
            }
        }
    }

    private async Task<Album> UploadAlbum()
    {
        var album = await Ctx.Albums.GetAlbum(Ctx.Session.AlbumId);
        if (album != null && await Ctx.Albums.CanManage(album, Ctx.UserId)) return album;
        Ctx.Session.Mode = Mode.Idle;
        return null;
    }

    private string Prompt(Album album) =>
        Ctx.F(LocKey.SongPrompt, Text.H(album.Title)) + (album.SongFileId != null ? Ctx.T(LocKey.SongPromptReplace) : "");

    private ButtonForm PromptButtons(Album album)
    {
        var bf = new ButtonForm();
        if (album.SongFileId != null) bf.AddButtonRow(Ctx.T(LocKey.ButtonRemoveSong), $"sngdel:{album.Id}");
        bf.AddButtonRow(Ctx.T(LocKey.ButtonCancelAction), $"mg:{album.Id}");
        return bf;
    }
}
