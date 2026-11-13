// Copyright (c) Redis, Inc.

using Microsoft.Agents.AI;
using Redis.Iris.AgentFramework;

namespace RedisIrisDevUI;

// Development sample only: proposals expire and are lost on restart, like the in-memory agent sessions.
internal sealed class MemoryProposalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Add(string userKey, RedisIrisMemoryWriteProposal proposal, AgentSession? session)
    {
        lock (_gate)
        {
            foreach (var id in _entries.Where(pair => pair.Value.Proposal.ExpiresAt <= DateTimeOffset.UtcNow && !pair.Value.Approved).Select(pair => pair.Key).ToArray())
                _entries.Remove(id);
            if (_entries.Count >= 1000 || _entries.Values.Count(entry => entry.UserKey == userKey) >= 100)
                throw new InvalidOperationException("Too many pending memory proposals. Wait for old proposals to expire.");
            _entries.Add(proposal.Id, new(userKey, proposal, session));
        }
    }

    public object[] List(string userKey)
    {
        lock (_gate)
            return _entries.Values.Where(entry => entry.UserKey == userKey && entry.Proposal.ExpiresAt > DateTimeOffset.UtcNow && entry.Result is null)
                .Select(entry => Describe(entry.Proposal)).ToArray();
    }

    public static object Describe(RedisIrisMemoryWriteProposal proposal) => new
    {
        proposal.Id,
        kind = proposal.Kind.ToString(),
        proposal.MemoryId,
        proposal.Text,
        proposal.MemoryType,
        proposal.Topics,
        proposal.Attributes,
        proposal.ExpectedRevision,
        proposal.ExpiresAt,
    };

    public bool IsApproved(string userKey, RedisIrisMemoryWriteProposal proposal)
    {
        lock (_gate)
            return _entries.TryGetValue(proposal.Id, out var entry) && entry.UserKey == userKey &&
                ReferenceEquals(entry.Proposal, proposal) && entry.Approved;
    }

    public async Task<RedisIrisMemoryWriteResult> SaveAsync(string userKey, string id, RedisIrisLongTermMemoryWriter writer, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry!) || entry.UserKey != userKey || entry.Proposal.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new KeyNotFoundException("Memory proposal not found or expired.");
        }
        await entry.SaveLock.WaitAsync(cancellationToken);
        try
        {
            if (entry.Result is not null) return entry.Result;
            lock (_gate) entry.Approved = true;
            try
            {
                var result = await writer.ExecuteAsync(entry.Proposal, entry.Session, cancellationToken);
                lock (_gate) entry.Result = result;
                return result;
            }
            finally { lock (_gate) entry.Approved = false; }
        }
        finally { entry.SaveLock.Release(); }
    }

    private sealed class Entry(string userKey, RedisIrisMemoryWriteProposal proposal, AgentSession? session)
    {
        public string UserKey { get; } = userKey;
        public RedisIrisMemoryWriteProposal Proposal { get; } = proposal;
        public AgentSession? Session { get; } = session;
        public bool Approved { get; set; }
        public RedisIrisMemoryWriteResult? Result { get; set; }
        public SemaphoreSlim SaveLock { get; } = new(1, 1);
    }
}
