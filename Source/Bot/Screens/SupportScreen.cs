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
using nAlbum.Services;
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
/// Support conversations. A user writes to the admins from the help screen and can reply or close; admins list
/// all conversations with their history, answer in answer mode or by replying to a message, and can close them.
/// Every admin callback re-checks the admin right; users can only touch their own conversations.
/// </summary>
public sealed class SupportScreen : Screen
{
    private const int ListPage = 8;
    private const int HistoryTake = 40;
    private const int HistoryChars = 3000;

    public SupportScreen(BotContext ctx) : base(ctx) { }

    public override IReadOnlyCollection<string> Callbacks => new[] { "sup", "supr", "supc", "acv", "acd", "acr", "acc", "auc" };
    public override IReadOnlyCollection<Mode> TextModes => new[] { Mode.Support, Mode.AdminReply };

    private bool IsAdmin => Ctx.Config.IsAdmin(Ctx.UserId);

    public override async Task OnCallback(MessageResult m, string[] p)
    {
        if (p[0] is "acv" or "acd" or "acr" or "acc" or "auc" && !IsAdmin)
        {
            await m.ConfirmAction(Ctx.T(LocKey.StatsNotAllowed));
            return;
        }

        switch (p[0])
        {
            case "sup":
                await m.ConfirmAction();
                await OpenSupport(m);
                break;

            case "supr":
            case "supc":
            {
                var conversation = await Ctx.Support.GetAsync(Text.Long(p, 1));
                if (conversation == null || conversation.UserId != Ctx.UserId)
                {
                    await m.ConfirmAction(Ctx.T(LocKey.ErrorNotAllowed));
                    return;
                }

                await m.ConfirmAction();
                if (conversation.Status != ConversationStatus.Open)
                {
                    await Ctx.Ui.Show(m, Ctx.T(LocKey.SupportAlreadyClosed), HelpButtons());
                    return;
                }

                if (p[0] == "supr") await ShowUserPrompt(m, conversation.Id, false);
                else await CloseByUser(m, conversation);
                break;
            }

            case "acv":
                await m.ConfirmAction();
                await ShowList(m, Text.Int(p, 1));
                break;

            case "acd":
            {
                var conversation = await Ctx.Support.GetAsync(Text.Long(p, 1));
                if (conversation == null)
                {
                    await m.ConfirmAction(Ctx.T(LocKey.AdminConvGone));
                    return;
                }

                await m.ConfirmAction();
                Ctx.Session.Mode = Mode.Idle;
                await ShowHistory(m, conversation, Text.Int(p, 2));
                break;
            }

            case "acr":
            {
                var conversation = await Ctx.Support.GetAsync(Text.Long(p, 1));
                if (conversation == null)
                {
                    await m.ConfirmAction(Ctx.T(LocKey.AdminConvGone));
                    return;
                }

                await m.ConfirmAction();
                if (conversation.Status != ConversationStatus.Open)
                {
                    await ShowHistory(m, conversation, Text.Int(p, 2));
                    return;
                }

                await StartAnswer(m, conversation.UserId, conversation.Id);
                break;
            }

            case "acc":
            {
                var conversation = await Ctx.Support.GetAsync(Text.Long(p, 1));
                if (conversation == null)
                {
                    await m.ConfirmAction(Ctx.T(LocKey.AdminConvGone));
                    return;
                }

                await m.ConfirmAction(Ctx.T(LocKey.AdminConvClosedDone));
                Ctx.Session.Mode = Mode.Idle;
                if (await Ctx.Support.CloseAsync(conversation.Id))
                    await Push(conversation.UserId, 0, l => Ctx.Localization.T(l, LocKey.SupportClosedByAdmin),
                        l => HelpButtons(l));
                await ShowList(m, Text.Int(p, 2));
                break;
            }

            case "auc": // from the user list: start (or continue) a conversation with that user
            {
                var userId = Text.Long(p, 1);
                if (await Ctx.Users.GetOverviewAsync(userId) == null)
                {
                    await m.ConfirmAction(Ctx.T(LocKey.AdminUserGone));
                    return;
                }

                await m.ConfirmAction();
                var open = await Ctx.Support.GetOpenForUserAsync(userId);
                await StartAnswer(m, userId, open?.Id ?? 0);
                break;
            }
        }
    }

    public override async Task OnText(MessageResult m, string text)
    {
        if (Ctx.Session.Mode == Mode.AdminReply) await OnAdminMessage(text, 0);
        else await OnUserMessage(text, 0);
    }

    public override async Task OnMedia(DataResult data)
    {
        var prompt = Ctx.Session.Mode == Mode.AdminReply ? await AnswerPrompt() : (null, null);
        if (Ctx.Session.Mode == Mode.AdminReply)
            await Ctx.Ui.ShowMenu(Ctx.T(LocKey.SupportOnlyText) + "\n\n" + prompt.Item1, prompt.Item2);
        else
            await Ctx.Ui.ShowMenu(Ctx.T(LocKey.SupportOnlyText) + "\n\n" + UserPromptText(Ctx.Session.ConversationId != 0),
                UserButtons(Ctx.Session.ConversationId));
    }

    /// <summary>
    /// A Telegram reply to a message the bot sent about a conversation continues that conversation,
    /// whatever the current mode. False when the replied message is not one of those.
    /// </summary>
    public async Task<bool> TryHandleReply(Message msg)
    {
        var repliedTo = msg?.ReplyToMessage;
        if (repliedTo == null) return false;
        var conversationId = await Ctx.Support.FindNoticeAsync(Ctx.UserId, repliedTo.MessageId);
        if (conversationId == 0) return false;

        var conversation = await Ctx.Support.GetAsync(conversationId);
        if (msg.Text == null)
        {
            await Ctx.Ui.Say(Ctx.T(LocKey.SupportOnlyText));
            return true;
        }

        if (conversation == null || !IsAdmin && conversation.UserId != Ctx.UserId) return false;
        if (conversation.Status != ConversationStatus.Open)
        {
            await Ctx.Ui.Say(Ctx.T(LocKey.SupportAlreadyClosed), IsAdmin ? HistoryButtons(conversation, 0) : HelpButtons());
            return true;
        }

        Ctx.Session.ConversationId = conversation.Id;
        if (IsAdmin)
        {
            Ctx.Session.ConversationUserId = conversation.UserId;
            await OnAdminMessage(msg.Text, conversation.Id);
        }
        else
        {
            await OnUserMessage(msg.Text, conversation.Id);
        }

        return true;
    }

    // -------------------------------------------------------------- user side
    private ButtonForm HelpButtons(string language = null)
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(language == null ? Ctx.T(LocKey.ButtonBack) : Ctx.Localization.T(language, LocKey.ButtonBack), "help");
        return bf;
    }

    private ButtonForm UserButtons(long conversationId)
    {
        var bf = new ButtonForm();
        if (conversationId != 0) bf.AddButtonRow(Ctx.T(LocKey.ButtonCloseConversation), $"supc:{conversationId}");
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), "help");
        return bf;
    }

    private string UserPromptText(bool open) => Ctx.T(open ? LocKey.SupportOpenPrompt : LocKey.SupportPrompt);

    private async Task OpenSupport(MessageResult m)
    {
        if (IsAdmin || Ctx.Config.AdminIds.Count == 0)
        {
            await Ctx.Ui.Show(m, Ctx.T(LocKey.SupportUnavailable), HelpButtons());
            return;
        }

        var open = await Ctx.Support.GetOpenForUserAsync(Ctx.UserId);
        await ShowUserPrompt(m, open?.Id ?? 0, open != null);
    }

    private async Task ShowUserPrompt(MessageResult m, long conversationId, bool open)
    {
        Ctx.Session.Mode = Mode.Support;
        Ctx.Session.ConversationId = conversationId;
        await Ctx.Ui.Show(m, UserPromptText(open || conversationId != 0), UserButtons(conversationId));
    }

    private async Task CloseByUser(MessageResult m, Conversation conversation)
    {
        Ctx.Session.Mode = Mode.Idle;
        Ctx.Session.ConversationId = 0;
        if (await Ctx.Support.CloseAsync(conversation.Id))
        {
            var name = await UserLabel(conversation.UserId);
            foreach (var adminId in Ctx.Config.AdminIds)
                await Push(adminId, conversation.Id, l => Ctx.Localization.F(l, LocKey.AdminConvClosedByUser, conversation.Id, Text.H(name)), null);
        }

        await Ctx.Ui.Show(m, Ctx.T(LocKey.SupportClosed), HelpButtons());
    }

    private async Task OnUserMessage(string raw, long forcedConversationId)
    {
        var s = Ctx.Session;
        if (IsAdmin || Ctx.Config.AdminIds.Count == 0)
        {
            s.Mode = Mode.Idle;
            await Ctx.Ui.Say(Ctx.T(LocKey.SupportUnavailable), HelpButtons());
            return;
        }

        var text = Text.StripControl(raw).Trim();
        var id = forcedConversationId != 0 ? forcedConversationId : s.ConversationId;
        var prompt = UserPromptText(id != 0);
        if (text.Length == 0) return;
        if (text.Length > SupportService.MaxTextLength)
        {
            await Ctx.Ui.ShowMenu(Ctx.F(LocKey.SupportTooLong, SupportService.MaxTextLength) + "\n\n" + prompt, UserButtons(id));
            return;
        }

        if (await Ctx.Support.IsFloodingAsync(Ctx.UserId))
        {
            await Ctx.Ui.ShowMenu(Ctx.T(LocKey.SupportSlowDown) + "\n\n" + prompt, UserButtons(id));
            return;
        }

        var existing = id == 0 ? null : await Ctx.Support.GetAsync(id);
        if (existing == null || existing.UserId != Ctx.UserId || existing.Status != ConversationStatus.Open) id = 0;

        var (conversation, _) = await Ctx.Support.AddMessageAsync(id, Ctx.UserId, false, text);
        s.Mode = Mode.Support;
        s.ConversationId = conversation.Id;

        var name = await UserLabel(Ctx.UserId);
        foreach (var adminId in Ctx.Config.AdminIds)
            await Push(adminId, conversation.Id,
                l => Ctx.Localization.F(l, LocKey.AdminNewMessageNotice, conversation.Id, Text.H(name), Text.H(text)),
                l => AdminNoticeButtons(l, conversation.Id));

        await Ctx.Ui.ShowMenu(Ctx.T(LocKey.SupportSent), UserButtons(conversation.Id));
    }

    // ------------------------------------------------------------- admin side
    private ButtonForm AdminNoticeButtons(string language, long conversationId)
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(new ButtonBase(Ctx.Localization.T(language, LocKey.ButtonAnswer), $"acr:{conversationId}:0"),
            new ButtonBase(Ctx.Localization.T(language, LocKey.ButtonHistory), $"acd:{conversationId}:0"));
        return bf;
    }

    private async Task<string> UserLabel(long userId)
    {
        var name = (await Ctx.Users.GetOverviewAsync(userId))?.User.Name;
        return string.IsNullOrWhiteSpace(name) ? Ctx.F(LocKey.MemberUnknown, userId) : name;
    }

    private static string Icon(Conversation c) =>
        c.Status == ConversationStatus.Closed ? "✅" : c.AwaitingAdmin ? "🔔" : "💬";

    private async Task ShowList(MessageResult m, int page)
    {
        page = Math.Max(page, 0);
        var (items, total) = await Ctx.Support.ListAsync(page * ListPage, ListPage);
        if (items.Count == 0 && total > 0) // page is out of range
        {
            page = (total - 1) / ListPage;
            (items, total) = await Ctx.Support.ListAsync(page * ListPage, ListPage);
        }

        var text = Ctx.T(LocKey.AdminConvTitle) + (total == 0 ? Ctx.T(LocKey.ListEmpty) : "");
        var bf = new ButtonForm();
        foreach (var it in items)
        {
            var name = string.IsNullOrWhiteSpace(it.UserName) ? Ctx.F(LocKey.MemberUnknown, it.Conversation.UserId) : it.UserName;
            bf.AddButtonRow($"{Icon(it.Conversation)} #{it.Conversation.Id} {Text.Truncate(name, 28)} · {it.Messages}",
                $"acd:{it.Conversation.Id}:{page}");
        }

        var nav = BotUi.PageRow(page, total, ListPage, n => $"acv:{n}");
        if (nav != null) bf.AddButtonRow(nav);
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), "admin");
        await Ctx.Ui.Show(m, text, bf);
    }

    private ButtonForm HistoryButtons(Conversation c, int page)
    {
        var bf = new ButtonForm();
        if (c.Status == ConversationStatus.Open)
            bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonAnswer), $"acr:{c.Id}:{page}"),
                new ButtonBase(Ctx.T(LocKey.ButtonCloseConversation), $"acc:{c.Id}:{page}"));
        bf.AddButtonRow(Ctx.T(LocKey.ButtonBack), $"acv:{page}");
        return bf;
    }

    private async Task<string> HistoryText(Conversation c)
    {
        var name = await UserLabel(c.UserId);
        var status = Ctx.T(c.Status == ConversationStatus.Open ? LocKey.ConvOpen : LocKey.ConvClosed);
        var head = Ctx.F(LocKey.AdminConvHeader, c.Id, Text.H(name), c.UserId, status);

        var (messages, hasMore) = await Ctx.Support.RecentMessagesAsync(c.Id, HistoryTake);
        var lines = new List<string>();
        var used = 0;
        for (var i = messages.Count - 1; i >= 0; i--) // newest first, until the message is full
        {
            var msg = messages[i];
            var when = msg.SentAt.ToString("MM-dd HH:mm");
            var who = msg.FromAdmin ? Ctx.F(LocKey.ConvMsgAdmin, when) : Ctx.F(LocKey.ConvMsgUser, Text.H(name), when);
            var line = $"{who}\n{Text.H(msg.Text)}";
            if (used + line.Length > HistoryChars) { hasMore = true; break; }
            used += line.Length;
            lines.Insert(0, line);
        }

        var body = string.Join("\n\n", lines);
        return head + (hasMore ? "\n" + Ctx.T(LocKey.ConvHistoryOmitted) : "") + "\n\n" + body;
    }

    private async Task ShowHistory(MessageResult m, Conversation c, int page) =>
        await Ctx.Ui.Show(m, await HistoryText(c), HistoryButtons(c, page));

    /// <summary>Answer mode: everything the admin sends goes to the user. conversationId 0 = starts with the first message.</summary>
    private async Task StartAnswer(MessageResult m, long userId, long conversationId)
    {
        Ctx.Session.Mode = Mode.AdminReply;
        Ctx.Session.ConversationId = conversationId;
        Ctx.Session.ConversationUserId = userId;
        var (text, bf) = await AnswerPrompt();
        await Ctx.Ui.Show(m, text, bf);
    }

    private async Task<(string Text, ButtonForm Buttons)> AnswerPrompt()
    {
        var s = Ctx.Session;
        var name = Text.H(await UserLabel(s.ConversationUserId));
        var bf = new ButtonForm();
        if (s.ConversationId != 0)
        {
            bf.AddButtonRow(new ButtonBase(Ctx.T(LocKey.ButtonHistory), $"acd:{s.ConversationId}:0"),
                new ButtonBase(Ctx.T(LocKey.ButtonStopAnswering), $"acd:{s.ConversationId}:0"));
            return (Ctx.F(LocKey.AdminAnswerPrompt, s.ConversationId, name), bf);
        }

        bf.AddButtonRow(Ctx.T(LocKey.ButtonStopAnswering), $"auu:{s.ConversationUserId}:0");
        return (Ctx.F(LocKey.AdminAnswerNew, name), bf);
    }

    private async Task OnAdminMessage(string raw, long forcedConversationId)
    {
        var s = Ctx.Session;
        if (!IsAdmin)
        {
            s.Mode = Mode.Idle;
            return;
        }

        var text = Text.StripControl(raw).Trim();
        if (text.Length == 0) return;

        var id = forcedConversationId != 0 ? forcedConversationId : s.ConversationId;
        var conversation = id == 0 ? null : await Ctx.Support.GetAsync(id);
        var userId = conversation?.UserId ?? s.ConversationUserId;
        if (userId == 0 || id != 0 && conversation == null)
        {
            s.Mode = Mode.Idle;
            await Ctx.Ui.Say(Ctx.T(LocKey.AdminConvGone));
            return;
        }

        if (conversation != null && conversation.Status != ConversationStatus.Open)
        {
            s.Mode = Mode.Idle;
            await Ctx.Ui.Say(Ctx.T(LocKey.SupportAlreadyClosed), HistoryButtons(conversation, 0));
            return;
        }

        var forced = forcedConversationId != 0;
        if (!forced) s.ConversationUserId = userId;
        if (text.Length > SupportService.MaxTextLength)
        {
            var (tooLong, buttons) = await AnswerPrompt();
            await Ctx.Ui.ShowMenu(Ctx.F(LocKey.SupportTooLong, SupportService.MaxTextLength) + "\n\n" + tooLong, buttons);
            return;
        }

        var (updated, _) = await Ctx.Support.AddMessageAsync(id, userId, true, text);
        if (!forced) s.ConversationId = updated.Id;
        var delivered = await Push(userId, updated.Id,
            l => Ctx.Localization.F(l, LocKey.SupportFromAdmin, Text.H(text)),
            l => UserReplyButtons(l, updated.Id));
        var note = Ctx.T(delivered ? LocKey.AdminAnswerSent : LocKey.AdminAnswerNotDelivered);

        if (forced)
        {
            await Ctx.Ui.Say(note, HistoryButtons(updated, 0));
            return;
        }

        s.Mode = Mode.AdminReply;
        var (prompt, bf) = await AnswerPrompt();
        await Ctx.Ui.ShowMenu(note + "\n\n" + prompt, bf);
    }

    private ButtonForm UserReplyButtons(string language, long conversationId)
    {
        var bf = new ButtonForm();
        bf.AddButtonRow(new ButtonBase(Ctx.Localization.T(language, LocKey.ButtonReply), $"supr:{conversationId}"),
            new ButtonBase(Ctx.Localization.T(language, LocKey.ButtonCloseConversation), $"supc:{conversationId}"));
        return bf;
    }

    // ----------------------------------------------------------- delivery
    /// <summary>
    /// Sends a message to another chat in that user's language and remembers it as part of the conversation,
    /// so a Telegram reply to it continues the conversation. False when the user can't be reached.
    /// </summary>
    private async Task<bool> Push(long chatId, long conversationId, Func<string, string> text, Func<string, ButtonForm> buttons)
    {
        var language = Ctx.Localization.Resolve(await Ctx.Preferences.GetLanguageAsync(chatId), null);
        var bf = buttons?.Invoke(language);
        try
        {
            var msg = await Ctx.Device.Dispatch(a => a.SendMessage(chatId, Text.WithMinWidth(text(language), bf),
                parseMode: ParseMode.Html,
                replyMarkup: bf == null ? null : new InlineKeyboardMarkup(bf.ToInlineButtonArray())));
            if (conversationId != 0 && msg != null) await Ctx.Support.AddNoticeAsync(chatId, msg.MessageId, conversationId);
            return true;
        }
        catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Support message to {chatId} failed: {ex.Message}");
            return false;
        }
    }
}
