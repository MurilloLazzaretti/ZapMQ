namespace ZapMQ.Core;

/// <summary>
/// Messages that were never delivered or never confirmed, kept by queue name so that they
/// outlive the queue itself. Nothing consumes from here: entries only leave by hand or by limit.
/// </summary>
internal sealed class DeadLetterStore(TimeProvider time)
{
    private readonly object _gate = new();
    // Oldest first.
    private readonly Dictionary<string, LinkedList<DeadLetter>> _byQueue = new(StringComparer.Ordinal);

    private long _total;

    /// <summary>
    /// How many letters were ever kept, including the ones no longer here.
    /// </summary>
    public long Total => Interlocked.Read(ref _total);

    public void Add(DeadLetter letter, int limit, TimeSpan maxAge)
    {
        if (limit <= 0 || maxAge <= TimeSpan.Zero)
            return;

        lock (_gate)
        {
            if (!_byQueue.TryGetValue(letter.Queue, out var letters))
                _byQueue[letter.Queue] = letters = new LinkedList<DeadLetter>();

            letters.AddLast(letter);
            Interlocked.Increment(ref _total);
            while (letters.Count > limit)
                letters.RemoveFirst();
        }
    }

    public void Prune(Func<string, (int Limit, TimeSpan MaxAge)> limitsOf)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            foreach (var (queue, letters) in _byQueue)
            {
                var (limit, maxAge) = limitsOf(queue);
                while (letters.First is { } oldest && (letters.Count > limit || now - oldest.Value.DiedAt > maxAge))
                    letters.RemoveFirst();

                if (letters.Count == 0)
                    _byQueue.Remove(queue);
            }
        }
    }

    public IReadOnlyList<DeadLetterSummary> Summarize()
    {
        lock (_gate)
        {
            return _byQueue
                .Select(pair => new DeadLetterSummary(
                    pair.Key,
                    pair.Value.Count(l => l.Reason == DeadLetterReason.Expired),
                    pair.Value.Count(l => l.Reason == DeadLetterReason.NotConsumed),
                    pair.Value.Count(l => l.Reason == DeadLetterReason.Unconfirmed)))
                .OrderBy(summary => summary.Queue, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// Newest first.
    /// </summary>
    public IReadOnlyList<DeadLetter> List(string queue)
    {
        lock (_gate)
            return _byQueue.TryGetValue(queue, out var letters) ? letters.Reverse().ToList() : [];
    }

    public DeadLetter? Find(string queue, string id)
    {
        lock (_gate)
            return _byQueue.TryGetValue(queue, out var letters) ? letters.FirstOrDefault(l => l.Id == id) : null;
    }

    public DeadLetter? Remove(string queue, string id)
    {
        lock (_gate)
        {
            if (!_byQueue.TryGetValue(queue, out var letters))
                return null;

            for (var node = letters.First; node is not null; node = node.Next)
            {
                if (node.Value.Id != id)
                    continue;

                letters.Remove(node);
                if (letters.Count == 0)
                    _byQueue.Remove(queue);
                return node.Value;
            }
            return null;
        }
    }

    public int Clear(string queue)
    {
        lock (_gate)
            return _byQueue.Remove(queue, out var letters) ? letters.Count : 0;
    }
}
