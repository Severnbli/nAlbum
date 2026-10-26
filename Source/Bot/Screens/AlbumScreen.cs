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
using nAlbum.Services;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using nAlbum.Localization;

namespace nAlbum.Bot.Screens;

public sealed class AlbumScreen : Screen
{
    public AlbumScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "al", "mg", "new", "ren", "del", "delok" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.NewTitle, Mode.Rename };
    public override IReadOnlyCollection<string> Commands => new[] { "/new" };

    public override Task OnCommand(MessageResult m, string command, IReadOnlyList<string> args) => StartNew(m);

    public override async Task OnText(MessageResult m, string text)
    {
        await Ctx.Ui.TryDelete(m.MessageId); // the prompt is replaced by the result, keep the chat tidy
        switch (Ctx.Session.Mode)
        {
            case Mode.NewTitle:
            {
                var title = Text.CleanTitle(text);
                if (title.Length == 0)
                {
                    await Ctx.Ui.ShowMenu(Ctx.T(LocKey.AlbumTitleEmpty), NewCancelButtons());
                    return;
                }

                Ctx.Session.Mode = Mode.Idle;
                var album = await Ctx.Albums.CreateAlbum(Ctx.UserId, title);
                await Ctx.Stats.IncrementAsync(StatKeys.AlbumsCreated);
                var (card, bf) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Say(Ctx.T(LocKey.AlbumCreated) + card, bf);
                return;
            }

            case Mode.Rename:
            {
                var title = Text.CleanTitle(text);
                if (title.Length == 0)
                {
                    await Ctx.Ui.ShowMenu(Ctx.T(LocKey.AlbumTitleEmpty), CancelButtons(Ctx.Session.AlbumId));
                    return;
                }

                Ctx.Session.Mode = Mode.Idle;
                var album = await Ctx.Albums.GetAlbum(Ctx.Session.AlbumId);
                if (album == null || !await Ctx.Albums.CanManage(album, Ctx.UserId)) return;
                await Ctx.Albums.Rename(album.Id, title);
                album.Title = title;
                var (card, bf) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Say(card, bf);
                return;
            }
        }
    }

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        switch (p[0])
        {
            case "new":
                await m.ConfirmAction();
                await StartNew(m);
                break;

            case "al":
            {
                Ctx.Session.Mode = Mode.Idle;
                var album = await Ctx.Ui.ViewableAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                await Ctx.View.ClearView(m.MessageId);
                var (card, bf) = await Ctx.Cards.Build(album);
                await Ctx.Ui.Show(m, card, bf);
                break;
            }

            case "mg":
            {
                Ctx.Session.Mode = Mode.Idle;
                var album = await Ctx.Ui.ManagedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                var (text, bf) = await Ctx.Cards.BuildManage(album);
                await Ctx.Ui.Show(m, text, bf);
                break;
            }

            case "ren":
            {
                var album = await Ctx.Ui.ManagedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                Ctx.Session.Mode = Mode.Rename;
                Ctx.Session.AlbumId = album.Id;
                await Ctx.Ui.Show(m, Ctx.T(LocKey.AlbumRenamePrompt), CancelButtons(album.Id));
                break;
            }

            case "del":
            {
                var album = await Ctx.Ui.OwnedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction();
                var bf = new ButtonForm();
                bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonConfirmDelete), $"delok:{album.Id}"), new ButtonBase(Ctx.T(LocKey.ButtonCancel), $"mg:{album.Id}"));
                await Ctx.Ui.Show(m, Ctx.F(LocKey.AlbumDeleteConfirm, Text.H(album.Title)), bf);
                break;
            }

            case "delok":
            {
                var album = await Ctx.Ui.OwnedAlbum(m, p);
                if (album == null) return;
                await m.ConfirmAction(Ctx.T(LocKey.AlbumDeleted));
                var media = await Ctx.Albums.DeleteAlbum(album.Id);
                await Ctx.Stats.IncrementAsync(StatKeys.AlbumsDeleted);
                await Ctx.Stats.IncrementAsync(StatKeys.MediaDeleted, media);
                await Ctx.Ui.ShowList(m, "mine", 0);
                break;
            }
        }
    }

    private async Task StartNew(MessageResult m)
    {
        Ctx.Session.Mode = Mode.NewTitle;
        await Ctx.Ui.Show(m, Ctx.T(LocKey.AlbumTitlePrompt), NewCancelButtons());
    }

    private ButtonForm NewCancelButtons()
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(Ctx.T(LocKey.ButtonCancelAction), "albums");
        return bf;
    }

    private ButtonForm CancelButtons(long albumId)
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(Ctx.T(LocKey.ButtonCancelAction), $"mg:{albumId}");
        return bf;
    }
}
