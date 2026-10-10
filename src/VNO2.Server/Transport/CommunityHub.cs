using System.Collections.Concurrent;
using VNO2.Contracts.Community;
using VNO2.Server.Domain;
using VNO2.Server.Persistence;

namespace VNO2.Server.Transport;

public sealed class CommunityHub(CommunityStore store, CommunityOptions options) : IDisposable
{
    private readonly ConcurrentDictionary<string, SocketSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> roomAttempts = new(StringComparer.Ordinal);
    private int? registeredCapacity;
    private readonly SemaphoreSlim dispatch = new(1, 1);
    public int ConnectionCount => sessions.Count;
    public int OnlineUsers => sessions.Values.Select(s => s.AccountId).Distinct(StringComparer.Ordinal).Count();

    public async Task InitializeAsync(SocketSession session, string accountId, string displayName, string? guestToken, string requestId, int? verifiedCapacity = null)
    {
        await dispatch.WaitAsync(session.Cancellation);
        try
        {
            if (verifiedCapacity is < 1 or > 10000) throw new DomainException("authentication_failed", "The registered server capacity is invalid");
            if (verifiedCapacity != null) registeredCapacity = verifiedCapacity;
            var capacity = verifiedCapacity ?? Math.Min(options.LocalCapacity, registeredCapacity ?? options.LocalCapacity);
            if (!sessions.Values.Any(existing => existing.AccountId == accountId) && OnlineUsers >= capacity)
                throw new DomainException("server_full", "This community has reached its distinct-account capacity");
            if (sessions.Values.Count(s => s.AccountId == accountId) >= 4)
                throw new DomainException("connection_limit", "This account already has four community connections");
            session.AccountId = accountId;
            var snapshot = await store.SnapshotAsync(accountId, session.Cancellation);
            session.Send(ProtocolJson.Message("welcome", requestId, snapshot.Sequence, new CommunityWelcome(accountId, displayName, guestToken, snapshot)));
            sessions[session.Id] = session;
        }
        finally { dispatch.Release(); }
    }

    public void Remove(SocketSession session) => sessions.TryRemove(session.Id, out _);

    public async Task ExecuteAsync(SocketSession session, CommunityRequest request)
    {
        await dispatch.WaitAsync(session.Cancellation);
        try
        {
            if (request.Action == "room.join")
            {
                var now = DateTimeOffset.UtcNow;
                if (!roomAttempts.TryGetValue(session.AccountId!, out var attempts)) roomAttempts[session.AccountId!] = attempts = new();
                while (attempts.TryPeek(out var oldest) && oldest < now.AddMinutes(-1)) attempts.Dequeue();
                if (attempts.Count >= 10) throw new DomainException("room_rate_limited", "Please wait before trying another room password or entry");
                attempts.Enqueue(now);
            }
            var result = await store.ExecuteAsync(session.AccountId!, request, session.Cancellation);
            session.Send(result.Reply);
            // A committed action must reach the other participants even if its sender disconnects
            await PublishCoreAsync(result.Events, CancellationToken.None);
            if (!session.Cancellation.IsCancellationRequested && (request.Action == "snapshot.get" || result.Events.Count == 0))
            {
                var snapshot = await store.SnapshotAsync(session.AccountId!, CancellationToken.None);
                session.Send(ProtocolJson.Message("snapshot", request.RequestId, snapshot.Sequence, snapshot));
            }
            foreach (var target in sessions.Values.Where(s => result.DisconnectAccountIds.Contains(s.AccountId!, StringComparer.Ordinal))) target.Disconnect();
        }
        finally { dispatch.Release(); }
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await dispatch.WaitAsync(cancellationToken);
        try { await PublishCoreAsync(await store.ExpireTimersAsync(sessions.Values.Select(s => s.AccountId!).ToHashSet(StringComparer.Ordinal), cancellationToken), cancellationToken); }
        finally { dispatch.Release(); }
    }

    private async Task PublishCoreAsync(IReadOnlyList<AcceptedEvent> events, CancellationToken cancellationToken)
    {
        foreach (var accepted in events)
            foreach (var session in sessions.Values.Where(s => accepted.Audience.Contains(s.AccountId!, StringComparer.Ordinal)))
                session.Send(ProtocolJson.Message("event", null, accepted.Event.Sequence, accepted.Event));
        if (events.Count == 0) return;
        // Every connected account gets its own filtered snapshot, never another account's private resource state
        foreach (var group in sessions.Values.GroupBy(s => s.AccountId!))
        {
            try
            {
                var snapshot = await store.SnapshotAsync(group.Key, cancellationToken);
                var message = ProtocolJson.Message("snapshot", null, snapshot.Sequence, snapshot);
                foreach (var session in group) session.Send(message);
            }
            catch (DomainException) { foreach (var session in group) session.Disconnect(); }
        }
    }

    public void Dispose() => dispatch.Dispose();
}
