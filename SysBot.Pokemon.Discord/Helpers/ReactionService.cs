using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Timers;
using Discord;
using Discord.WebSocket;

public class ReactionService
{
    private readonly DiscordSocketClient _client;
    private readonly Dictionary<ulong, Func<SocketReaction, Task>> _reactionActions;
    private readonly Dictionary<ulong, Timer> _reactionTimers;

    public ReactionService(DiscordSocketClient client)
    {
        _client = client;
        _reactionActions = new Dictionary<ulong, Func<SocketReaction, Task>>();
        _reactionTimers = new Dictionary<ulong, Timer>();

        _client.ReactionAdded += OnReactionAddedAsync;
    }

    public void AddReactionHandler(ulong messageId, Func<SocketReaction, Task> handler)
    {
        _reactionActions[messageId] = handler;

        var timer = new Timer(120000); // 2 minutes
        timer.Elapsed += (sender, args) => RemoveReactionHandler(messageId);
        timer.AutoReset = false; // Trigger only once
        timer.Start();

        _reactionTimers[messageId] = timer;
    }

    public void RemoveReactionHandler(ulong messageId)
    {
        if (_reactionActions.ContainsKey(messageId))
        {
            _reactionActions.Remove(messageId);
        }

        // Stop and dispose of the timer if it exists
        if (_reactionTimers.TryGetValue(messageId, out var timer))
        {
            timer.Stop();
            timer.Dispose();
            _reactionTimers.Remove(messageId);
        }
    }

    private async Task OnReactionAddedAsync(Cacheable<IUserMessage, ulong> cachedMessage, Cacheable<IMessageChannel, ulong> cachedChannel, SocketReaction reaction)
    {
        if (_reactionActions.TryGetValue(reaction.MessageId, out var handler))
        {
            await handler(reaction);
            RemoveReactionHandler(reaction.MessageId); // Remove handler after reaction
        }
    }
}
