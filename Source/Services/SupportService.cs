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

using Microsoft.EntityFrameworkCore;
using nAlbum.Data;

namespace nAlbum.Services;

/// <summary>Support conversations between users and admins. Queries are LINQ or bound parameters.</summary>
public sealed class SupportService
{
    public const int MaxTextLength = 2000;
    private const int RecentLimit = 8;                              // user messages
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);

    private readonly IDbContextFactory<AlbumDb> _factory;

    public SupportService(IDbContextFactory<AlbumDb> factory) { _factory = factory; }

    public async Task<Conversation> GetAsync(long id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<Conversation> GetOpenForUserAsync(long userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Conversations.AsNoTracking()
            .Where(c => c.UserId == userId && c.Status == ConversationStatus.Open)
            .OrderByDescending(c => c.Id).FirstOrDefaultAsync();
    }

    /// <summary>Appends a message to the conversation, or starts a new one when conversationId is 0.</summary>
    public async Task<(Conversation Conversation, bool Started)> AddMessageAsync(long conversationId, long userId, bool fromAdmin, string text)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var conversation = conversationId == 0
            ? null
            : await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.Status == ConversationStatus.Open);
        var started = conversation == null;
        if (started)
        {
            conversation = new Conversation { UserId = userId };
            db.Conversations.Add(conversation);
        }

        var now = DateTime.UtcNow;
        conversation.UpdatedAt = now;
        conversation.AwaitingAdmin = !fromAdmin;
        await db.SaveChangesAsync(); // gives a new conversation its id
        db.ConversationMessages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id, FromAdmin = fromAdmin, Text = text, SentAt = now
        });
        await db.SaveChangesAsync();
        return (conversation, started);
    }

    /// <summary>True when the user wrote too many messages in the last minute.</summary>
    public async Task<bool> IsFloodingAsync(long userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var since = DateTime.UtcNow - RecentWindow;
        var count = await (from m in db.ConversationMessages
                           join c in db.Conversations on m.ConversationId equals c.Id
                           where c.UserId == userId && !m.FromAdmin && m.SentAt > since
                           select m.Id).CountAsync();
        return count >= RecentLimit;
    }

    /// <summary>False when it was not open.</summary>
    public async Task<bool> CloseAsync(long id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Conversations.Where(c => c.Id == id && c.Status == ConversationStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ConversationStatus.Closed)
                .SetProperty(c => c.AwaitingAdmin, false)) > 0;
    }

    /// <summary>Open conversations first, those waiting for an answer on top, then the most recent.</summary>
    public async Task<(List<ConversationListItem> Items, int Total)> ListAsync(int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var total = await db.Conversations.CountAsync();
        var items = await (from c in db.Conversations.AsNoTracking()
                           join u in db.BotUsers.AsNoTracking() on c.UserId equals u.UserId into users
                           from u in users.DefaultIfEmpty()
                           orderby c.Status, c.AwaitingAdmin descending, c.UpdatedAt descending
                           select new ConversationListItem
                           {
                               Conversation = c,
                               UserName = u.Name,
                               Messages = db.ConversationMessages.Count(m => m.ConversationId == c.Id)
                           })
            .Skip(skip).Take(take).ToListAsync();
        return (items, total);
    }

    /// <summary>The newest messages in time order; the flag says whether older ones exist.</summary>
    public async Task<(List<ConversationMessage> Items, bool HasMore)> RecentMessagesAsync(long conversationId, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var items = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.Id).Take(take + 1).ToListAsync();
        var more = items.Count > take;
        items = items.Take(take).Reverse().ToList();
        return (items, more);
    }

    public async Task AddNoticeAsync(long chatId, int messageId, long conversationId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT OR REPLACE INTO ConversationNotices (ChatId, MessageId, ConversationId) VALUES ({chatId}, {messageId}, {conversationId})");
    }

    /// <summary>Forgets the bot messages of this chat about the conversation and returns their ids, so the caller can delete them.</summary>
    public async Task<List<int>> TakeNoticesAsync(long chatId, long conversationId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var ids = await db.ConversationNotices.AsNoTracking()
            .Where(n => n.ChatId == chatId && n.ConversationId == conversationId)
            .Select(n => n.MessageId).ToListAsync();
        if (ids.Count > 0)
            await db.ConversationNotices.Where(n => n.ChatId == chatId && n.ConversationId == conversationId).ExecuteDeleteAsync();
        return ids;
    }

    /// <summary>The conversation a bot message of this chat belongs to, or 0.</summary>
    public async Task<long> FindNoticeAsync(long chatId, int messageId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ConversationNotices.AsNoTracking()
            .Where(n => n.ChatId == chatId && n.MessageId == messageId)
            .Select(n => n.ConversationId).FirstOrDefaultAsync();
    }
}
