using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using StateleSSE.AspNetCore;

namespace StateleSSE.Backplane.Redis.Infrastructure;

/// <summary>
/// Redis-based implementation of ISseBackplane for horizontal scaling of SSE/realtime features.
/// Completely agnostic to domain - just handles groups and pub/sub.
/// Can be used for any realtime feature: todos, chats, notifications, quizzes, etc.
/// </summary>
public class RedisBackplane : ISseBackplane, IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;
    private readonly string _channelPrefix;

    // Local SSE connections (only for this server instance)
    // groupId -> subscriberId -> channel
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<object>>> _localSubscribers = new();

    public RedisBackplane(IConnectionMultiplexer redis, string channelPrefix = "backplane")
    {
        _redis = redis;
        _subscriber = redis.GetSubscriber();
        _channelPrefix = channelPrefix;

        // Subscribe to Redis pub/sub for ALL events on this channel
        _subscriber.Subscribe(
            (RedisChannel)$"{_channelPrefix}:events",
            async (channel, message) => await OnRedisMessage(message)
        );

        Console.WriteLine($"[RedisBackplane] Initialized with prefix '{_channelPrefix}'");
    }

    // ============================================
    // CLIENT SUBSCRIPTION (LOCAL SSE CONNECTIONS)
    // ============================================

    /// <summary>
    /// Subscribe a local client (SSE connection) to a group.
    /// Returns channel reader and unique subscriber ID.
    /// </summary>
    public (ChannelReader<object> reader, Guid subscriberId) Subscribe(string groupId)
    {
        var channel = Channel.CreateUnbounded<object>();
        var subscriberId = Guid.NewGuid();

        var channels = _localSubscribers.GetOrAdd(groupId, _ => new ConcurrentDictionary<Guid, Channel<object>>());
        channels.TryAdd(subscriberId, channel);

        Console.WriteLine($"[RedisBackplane] New subscriber {subscriberId} for group '{groupId}'. Total local: {channels.Count}");

        return (channel.Reader, subscriberId);
    }

    /// <summary>
    /// Unsubscribe a local client (cleanup when SSE connection closes).
    /// </summary>
    public void Unsubscribe(string groupId, Guid subscriberId)
    {
        if (_localSubscribers.TryGetValue(groupId, out var channels))
        {
            if (channels.TryRemove(subscriberId, out var channel))
            {
                channel.Writer.Complete();
                Console.WriteLine($"[RedisBackplane] Unsubscribed {subscriberId} from group '{groupId}'. Remaining local: {channels.Count}");
            }

            // Cleanup: Remove group entry if no subscribers left on this server
            if (channels.IsEmpty)
            {
                _localSubscribers.TryRemove(groupId, out _);
                Console.WriteLine($"[RedisBackplane] No local subscribers left for group '{groupId}', cleaning up");
            }
        }
    }

    // ============================================
    // BROADCASTING (CROSS-SERVER VIA REDIS)
    // ============================================

    /// <summary>
    /// Publish message to ALL servers in a group via Redis pub/sub.
    /// Message is broadcast to all servers, each server forwards to its local clients.
    /// </summary>
    public async Task PublishToGroup(string groupId, object message)
    {
        var envelope = new BackplaneEnvelope
        {
            GroupId = groupId,
            Payload = message,
            PublishedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(envelope);

        // Publish to Redis - all servers receive this
        await _subscriber.PublishAsync(
            (RedisChannel)$"{_channelPrefix}:events",
            json
        );

        Console.WriteLine($"[RedisBackplane] Published to Redis for group '{groupId}': {message.GetType().Name}");
    }

    /// <summary>
    /// Publish message to multiple groups at once.
    /// </summary>
    public async Task PublishToGroups(IEnumerable<string> groupIds, object message)
    {
        var tasks = groupIds.Select(groupId => PublishToGroup(groupId, message));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Publish message to ALL groups (broadcast to entire system).
    /// Use sparingly - this sends to everyone.
    /// </summary>
    public async Task PublishToAll(object message)
    {
        var envelope = new BackplaneEnvelope
        {
            GroupId = "*", // Wildcard for "all groups"
            Payload = message,
            PublishedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(envelope);

        await _subscriber.PublishAsync(
            (RedisChannel)$"{_channelPrefix}:events",
            json
        );

        Console.WriteLine($"[RedisBackplane] Published to ALL groups: {message.GetType().Name}");
    }

    // ============================================
    // INTERNAL: REDIS MESSAGE HANDLING
    // ============================================

    /// <summary>
    /// Called when Redis pub/sub message arrives (from ANY server, including this one).
    /// Forwards to local SSE clients in the target group.
    /// </summary>
    private async Task OnRedisMessage(RedisValue message)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<BackplaneEnvelope>(message.ToString());
            if (envelope == null) return;

            // Handle broadcast to all groups
            if (envelope.GroupId == "*")
            {
                await BroadcastToAllLocalGroups(envelope.Payload);
                return;
            }

            // Forward to local subscribers in this group only
            if (_localSubscribers.TryGetValue(envelope.GroupId, out var channels))
            {
                Console.WriteLine($"[RedisBackplane] Forwarding Redis event to {channels.Count} local subscribers of group '{envelope.GroupId}'");

                var tasks = channels.Values.Select(channel =>
                    channel.Writer.WriteAsync(envelope.Payload).AsTask()
                );

                await Task.WhenAll(tasks);
            }
            else
            {
                // This is fine - other servers might have subscribers for this group
                Console.WriteLine($"[RedisBackplane] Received Redis event for group '{envelope.GroupId}', but no local subscribers");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RedisBackplane] Error processing Redis message: {ex.Message}");
        }
    }

    private async Task BroadcastToAllLocalGroups(object payload)
    {
        var allTasks = new List<Task>();

        foreach (var (groupId, channels) in _localSubscribers)
        {
            Console.WriteLine($"[RedisBackplane] Broadcasting to {channels.Count} local subscribers in group '{groupId}'");

            var tasks = channels.Values.Select(channel =>
                channel.Writer.WriteAsync(payload).AsTask()
            );

            allTasks.AddRange(tasks);
        }

        await Task.WhenAll(allTasks);
    }

    // ============================================
    // STATS & DIAGNOSTICS
    // ============================================

    /// <summary>
    /// Get count of local subscribers for a group (only on THIS server).
    /// </summary>
    public int GetLocalSubscriberCount(string groupId)
    {
        return _localSubscribers.TryGetValue(groupId, out var channels) ? channels.Count : 0;
    }

    /// <summary>
    /// Get all group IDs that have local subscribers on THIS server.
    /// </summary>
    public IEnumerable<string> GetLocalGroups()
    {
        return _localSubscribers.Keys;
    }

    /// <summary>
    /// Get diagnostic info about this server's backplane state.
    /// </summary>
    public BackplaneDiagnostics GetDiagnostics()
    {
        return new BackplaneDiagnostics
        {
            TotalGroups = _localSubscribers.Count,
            TotalLocalSubscribers = _localSubscribers.Values.Sum(c => c.Count),
            Groups = _localSubscribers.Select(kvp => new GroupInfo
            {
                GroupId = kvp.Key,
                LocalSubscribers = kvp.Value.Count
            }).ToArray()
        };
    }

    // ============================================
    // CLEANUP
    // ============================================

    public void Dispose()
    {
        // Unsubscribe from Redis pub/sub
        _subscriber.Unsubscribe((RedisChannel)$"{_channelPrefix}:events");

        // Complete all local channels
        foreach (var groupChannels in _localSubscribers.Values)
        {
            foreach (var channel in groupChannels.Values)
            {
                channel.Writer.Complete();
            }
        }

        _localSubscribers.Clear();
        Console.WriteLine($"[RedisBackplane] Disposed (prefix: '{_channelPrefix}')");
    }
}

// ============================================
// ENVELOPE & DIAGNOSTICS
// ============================================

/// <summary>
/// Internal envelope for Redis pub/sub messages.
/// Contains group ID for routing and the actual payload.
/// </summary>
internal class BackplaneEnvelope
{
    public required string GroupId { get; init; }
    public required object Payload { get; init; }
    public DateTime PublishedAt { get; init; }
}

