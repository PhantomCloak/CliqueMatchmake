namespace Sukhoi;

using Lucene.Net.Search;

using QueryRung = (int AtSec, string Query);
using ParsedQueryRung = (int AtSec, string QueryString, Lucene.Net.Search.Query Query);
using MinMaxRung = (int AtSec, int Min, int Max);

public sealed class MatchmakerException : Exception
{
    public MatchmakerException(string message) : base(message) { }

    public const string InvalidQuery = "matchmaker query invalid";
    public const string DuplicateSession = "matchmaker duplicate session";
    public const string TooManyTickets = "matchmaker too many tickets";
    public const string InvalidPartyId = "matchmaker multiple tickets have to share same party id";
    public const string InvalidPartyMembers = "matchmaker multiple tickets have to share same members";
    public const string QueryPropertiesDiffer = "matchmaker queries must constrain the same properties";
    public const string BackfillRosterQueued = "matchmaker backfill roster already queued";
}

public class MatchmakerTicket
{
    public required string Ticket { get; init; }
    public required string OwningSessionId { get; init; }
    public int MinCount => MinMaxLadder[MinMaxRung].Min;
    public int MaxCount => MinMaxLadder[MinMaxRung].Max;
    public int Size { get; init; }
    public int CountMultiple = 1;
    public required string PartyId { get; init; }
    public required long CreatedAt { get; init; }

    public required ParsedQueryRung[] QueryLadder { get; init; }
    public required MinMaxRung[] MinMaxLadder { get; init; }

    public double SecondsWaited { get; set; }
    public int QueryRung { get; set; }
    public int MinMaxRung { get; set; }

    public string QueryString => QueryLadder[QueryRung].QueryString;
    public Query Query => QueryLadder[QueryRung].Query;

    public required Dictionary<string, object> Properties { get; init; }
    public HashSet<string> Members { get; set; } = new();
}

public partial class Matchmaker : IDisposable
{
    private MatchIndex _index = new();

    readonly Dictionary<string, MatchmakerTicket> _activeIndexes = new();
    readonly Dictionary<string, MatchmakerTicket> _indexes = new();

    readonly Dictionary<string, HashSet<string>> _sessionTickets = new();
    readonly Dictionary<string, HashSet<string>> _partySessions = new();

    readonly MatchmakerConfig _config;

    public int PoolSize => _indexes.Count;
    public int ActivePoolSize => _activeIndexes.Count;
    public MatchmakerConfig Config => _config;

    private readonly int _maxLadderRungs;

    private long _lastCreatedAt;

    public Matchmaker(MatchmakerConfig? config = null)
    {
        _config = config ?? new MatchmakerConfig();

        if (_config.MaxTicketPatienceInSec <= 0)
        {
            throw new ArgumentException(
                $"Invalid MaxTicketPatienceInSec ({_config.MaxTicketPatienceInSec}), must be > 0: "
                + "a ticket with no patience runs out before its first pass",
                nameof(config));
        }
        if (_config.MaxLadderRungs < 0)
        {
            throw new ArgumentException(
                $"Invalid MaxLadderRungs ({_config.MaxLadderRungs}), must be >= 0: "
                + "0 already means unbounded",
                nameof(config));
        }

        _maxLadderRungs = _config.MaxLadderRungs == 0 ? int.MaxValue : _config.MaxLadderRungs;
    }


    public (string Ticket, long CreatedAt) Add(HashSet<string> sessionIds, string ownerSessionId, string partyId, string query, Dictionary<string, object> properties, MinMaxRung[] minMaxLadder, int countMultiple = 1, DateTime? createdAt = null) =>
         AddTicket(sessionIds, ownerSessionId, partyId, [(0, query)], properties, minMaxLadder, _maxLadderRungs, countMultiple, createdAt);

    public (string Ticket, long CreatedAt) Add(HashSet<string> sessionIds, string ownerSessionId, string partyId, QueryRung[] queryLadder, Dictionary<string, object> properties, MinMaxRung[] minMaxLadder, int countMultiple = 1, DateTime? createdAt = null) =>
         AddTicket(sessionIds, ownerSessionId, partyId, queryLadder, properties, minMaxLadder, _maxLadderRungs, countMultiple, createdAt);

    public (string Ticket, long CreatedAt) AddBackfill(HashSet<string> sessionIds, string ownerSessionId, string partyId, string query, Dictionary<string, object> properties, MinMaxRung[] minMaxLadder, int countMultiple = 1, DateTime? createdAt = null) =>
        AddBackfill(sessionIds, ownerSessionId, partyId, [(0, query)], properties, minMaxLadder, countMultiple, createdAt);
 
    private (string Ticket, long CreatedAt) AddTicket(HashSet<string> sessionIds, string ownerSessionId, string partyId, QueryRung[] queryLadder, Dictionary<string, object> properties, MinMaxRung[] minMaxLadder, int minMaxRungCap, int countMultiple, DateTime? createdAt)
    {
        if (queryLadder.Length == 0 || minMaxLadder.Length == 0)
        {
            throw new ArgumentException("Invalid query or min-max range, it must be non-empty");
        }

        if (queryLadder.Length > _maxLadderRungs || minMaxLadder.Length > _maxLadderRungs)
        {
            throw new ArgumentException($"Invalid query or min-max range, it cannot exceed {_maxLadderRungs}");
        }

        if (queryLadder[0].AtSec != 0 || minMaxLadder[0].AtSec != 0)
        {
            throw new ArgumentException($"Query or min-max ladders should be start at 0");
        }

        if (queryLadder.Any(l => l.AtSec < 0 || l.AtSec > _config.MaxTicketPatienceInSec))
        {
            throw new ArgumentException($"Seconds given for query ladder should be between 0 and {_config.MaxTicketPatienceInSec}");
        }

        if (minMaxLadder.Any(x => x.AtSec < 0 || x.AtSec > _config.MaxTicketPatienceInSec))
        {
            throw new ArgumentException($"Seconds given for MinMax ladder should be between 0 and {_config.MaxTicketPatienceInSec}");
        }

        for (int i = 1; i < minMaxLadder.Length; i++)
        {
            if (minMaxLadder[i].AtSec <= minMaxLadder[i - 1].AtSec)
            {
                throw new ArgumentException($"Invalid minMax ladder, rung only can be narrower");
            }
        }
        for (int i = 1; i < queryLadder.Length; i++)
        {
            if (queryLadder[i].AtSec <= queryLadder[i - 1].AtSec)
            {
                throw new ArgumentException($"Invalid query ladder, rung only can be narrower");
            }
        }

        if (countMultiple == 0)
        {
            throw new ArgumentException("countMultiple must be bigger than 0", nameof(countMultiple));
        }

        for (int i = 0; i < minMaxLadder.Length; i++)
        {
            var minMaxRung = minMaxLadder[i];

            if (minMaxRung.Min < 2)
            {
                throw new ArgumentException(
                    $"Invalid minimum count at range rung {i}, must be >= 2", nameof(minMaxLadder));
            }
            if (minMaxRung.Max < minMaxRung.Min)
            {
                throw new ArgumentException(
                    $"Invalid maximum count at range rung {i}, must be >= minimum count", nameof(minMaxLadder));
            }
            if (minMaxRung.Max % countMultiple != 0)
            {
                throw new ArgumentException(
                    $"Invalid maximum count at range rung {i}, must be a multiple of {countMultiple}: a lobby is only seated at a size {countMultiple} divides, so {minMaxRung.Max} could never be seated",
                    nameof(minMaxLadder));
            }
            if (minMaxRung.Min % countMultiple != 0)
            {
                throw new ArgumentException(
                    $"Invalid minimum count at range rung {i}, must be a multiple of {countMultiple}: a lobby is only seated at a size {countMultiple} divides, so a floor of {minMaxRung.Min} already means {((minMaxRung.Min + countMultiple - 1) / countMultiple) * countMultiple}",
                    nameof(minMaxLadder));
            }
            if (sessionIds.Count > minMaxRung.Max)
            {
                throw new ArgumentException(
                    $"Invalid member count, must be < max count at range rung {i}", nameof(minMaxLadder));
            }
            if (i != 0 && minMaxRung.Min > minMaxLadder[i - 1].Min)
            {
                throw new ArgumentException(
                    $"Invalid minimum count at range rung {i}, must be <= the rung below it ({minMaxLadder[i - 1].Min}): a range ladder may only narrow",
                    nameof(minMaxLadder));
            }
            if (i != 0 && minMaxRung.Max > minMaxLadder[i - 1].Max)
            {
                throw new ArgumentException(
                    $"Invalid maximum count at range rung {i}, must be <= the rung below it ({minMaxLadder[i - 1].Max}): a range ladder may only narrow",
                    nameof(minMaxLadder));
            }
        }

        if (sessionIds.Count <= 0)
        {
            throw new ArgumentException("Invalid sessions, must be non-empty");
        }
        if (sessionIds.Count > 1 && partyId == "")
        {
            throw new ArgumentException("Invalid party id, must be non-empty if members more than 1");
        }
        if (!sessionIds.Contains(ownerSessionId))
        {
            throw new ArgumentException("Invalid session, session must be in member session");
        }
        if (partyId != "" && _partySessions.TryGetValue(partyId, out var existingPartySessions))
        {
            if (existingPartySessions.Count != sessionIds.Count || !existingPartySessions.All(x => sessionIds.Contains(x)))
            {
                throw new MatchmakerException(MatchmakerException.InvalidPartyMembers);
            }
        }

        foreach (var session in sessionIds)
        {
            if (_sessionTickets.TryGetValue(session, out var existingTickets))
            {
                if (existingTickets.Count >= _config.MaxTicketPerSession)
                {
                    throw new MatchmakerException(MatchmakerException.TooManyTickets);
                }

                if (!existingTickets.All(x => _indexes.TryGetValue(x, out var existingTicket) && existingTicket.PartyId == partyId))
                {
                    throw new MatchmakerException(MatchmakerException.InvalidPartyId);
                }

                if (!existingTickets.All(x => _indexes[x].Members.SetEquals(sessionIds)))
                {
                    throw new MatchmakerException(MatchmakerException.InvalidPartyMembers);
                }
            }
        }

        var (queryStrings, parsedQueries) = MatchQueryParser.ParseLadder(Array.ConvertAll(queryLadder, rung => rung.Query));

        // the second each rung was named on, carried alongside the query it parsed to, so the sweep
        // reads one array rather than two in step
        var parsedQueryLadder = new ParsedQueryRung[queryLadder.Length];
        for (int i = 0; i < queryLadder.Length; i++)
        {
            parsedQueryLadder[i] = (queryLadder[i].AtSec, queryStrings[i], parsedQueries[i]);
        }

        string ticket = Guid.NewGuid().ToString();

        long queuedAt = ((createdAt ?? DateTime.UtcNow).Ticks - DateTime.UnixEpoch.Ticks) * 100;
        long stampedAt = _lastCreatedAt = queuedAt > _lastCreatedAt ? queuedAt : _lastCreatedAt + 1;

        // built from what the caller wrote, after every rule above has been held against it

        var index = new MatchmakerTicket
        {
            Ticket = ticket,
            OwningSessionId = ownerSessionId,
            Size = sessionIds.Count,
            CountMultiple = countMultiple,
            PartyId = partyId,
            CreatedAt = stampedAt,
            QueryLadder = parsedQueryLadder,
            MinMaxLadder = minMaxLadder,
            Properties = new Dictionary<string, object>(properties, properties.Comparer),
            Members = new HashSet<string>(sessionIds, sessionIds.Comparer)
        };

        _index.Upsert(index);
        _indexes[ticket] = index;
        _activeIndexes[ticket] = index;

        foreach (var session in sessionIds)
        {
            if (!_sessionTickets.TryGetValue(session, out var tickets))
            {
                _sessionTickets[session] = tickets = new HashSet<string>();
            }
            tickets.Add(ticket);

            if (partyId != "")
            {
                if (!_partySessions.TryGetValue(partyId, out var partySessions))
                {
                    _partySessions[partyId] = partySessions = new HashSet<string>();
                }
                partySessions.Add(session);
            }
        }

        return (ticket, stampedAt);
    }

    private static void ComputeTicketRung(MatchmakerTicket ticket)
    {
        int queryRung = 0;
        while (queryRung + 1 < ticket.QueryLadder.Length
               && ticket.SecondsWaited >= ticket.QueryLadder[queryRung + 1].AtSec)
        {
            queryRung++;
        }

        int minMaxRung = 0;
        while (minMaxRung + 1 < ticket.MinMaxLadder.Length
               && ticket.SecondsWaited >= ticket.MinMaxLadder[minMaxRung + 1].AtSec)
        {
            minMaxRung++;
        }

        ticket.QueryRung = queryRung;
        ticket.MinMaxRung = minMaxRung;
    }

    public List<List<MatchmakerTicket>> Sweep(Dictionary<string, MatchmakerTicket> activeIndexSnapshot, Dictionary<string, MatchmakerTicket> indexSnapshot, DateTime now, out List<string> expired)
    {
        expired = new();

        var matched = new List<List<MatchmakerTicket>>();
        var consumed = new HashSet<string>();

        foreach (MatchmakerTicket ticket in indexSnapshot.Values)
        {
            ticket.SecondsWaited = Math.Max(0, (now - DateTime.UnixEpoch.AddTicks(ticket.CreatedAt / 100)).TotalSeconds);
            ComputeTicketRung(ticket);
        }

        using var search = _index.OpenSearcher();

        foreach (var (seedId, seed) in activeIndexSnapshot.OrderBy(kv => kv.Value.CreatedAt))
        {
            if (consumed.Contains(seed.Ticket))
            {
                continue;
            }

            if (seed.Members.Count >= seed.MinCount)
            {
                List<MatchmakerTicket> m = [seed];
                matched.Add(m);
                Consume(search, consumed, m);
                continue;
            }

            bool isLadderSpent = seed.QueryRung == seed.QueryLadder.Length - 1 && seed.MinMaxRung == seed.MinMaxLadder.Length - 1;

            if (seed.SecondsWaited >= _config.MaxTicketPatienceInSec || (seed.MinCount == seed.MaxCount && isLadderSpent))
            {
                expired.Add(seedId);
            }

            var possibleMatches = new List<List<MatchmakerTicket>>();
            List<MatchmakerTicket>? foundMatch = null;
            foreach (var candidateId in search.SearchTickets(seed))
            {
                if (candidateId == seedId)
                {
                    continue;
                }

                if (!indexSnapshot.TryGetValue(candidateId, out var candidate))
                {
                    continue;
                }

                if (seed.PartyId != "" && candidate.PartyId == seed.PartyId)
                {
                    continue;
                }

                if (seed.CountMultiple != candidate.CountMultiple)
                {
                    continue;
                }

                if (candidate.MinCount > seed.MaxCount)
                {
                    continue;
                }

                if (seed.Members.Overlaps(candidate.Members))
                {
                    continue;
                }

                if (!search.IsAcceptsTo(who: candidate, to: seed))
                {
                    continue;
                }

                List<MatchmakerTicket>? targetMatch = null;
                foreach (var possibleMatch in possibleMatches)
                {
                    if (possibleMatch.Sum(ticket => ticket.Size) + candidate.Size + seed.Size > seed.MaxCount)
                    {
                        continue;
                    }

                    if (possibleMatch.Any(ticket => ticket.Members.Overlaps(candidate.Members)))
                    {
                        continue;
                    }

                    if (!possibleMatch.All(ticket => search.IsAcceptsTo(who: candidate, to: ticket) && search.IsAcceptsTo(who: ticket, to: candidate)))
                    {
                        continue;
                    }

                    targetMatch = possibleMatch;
                    possibleMatch.Add(candidate);
                    break;
                }

                if (targetMatch == null)
                {
                    targetMatch = new List<MatchmakerTicket>() { candidate };
                    possibleMatches.Add(targetMatch);
                }

                if (targetMatch.Sum(ticket => ticket.Size) + seed.Size != seed.MaxCount)
                {
                    continue;
                }

                foundMatch = TryFinalizeCombo(seed, targetMatch);
                if (foundMatch != null)
                {
                    break;
                }
            }

            if (foundMatch == null)
            {
                foreach (var possibleMatch in possibleMatches)
                {
                    var seatable = TrimToSeatable(seed, possibleMatch);
                    if (seatable == null)
                    {
                        continue;
                    }

                    foundMatch = TryFinalizeCombo(seed, seatable);
                    if (foundMatch != null)
                    {
                        break;
                    }
                }
            }

            if (foundMatch == null)
            {
                continue;
            }

            matched.Add(foundMatch);
            Consume(search, consumed, foundMatch);
        }

        return matched;
    }

    private void Consume(SearcherScope search, HashSet<string> consumedTickets, List<MatchmakerTicket> match)
    {
        foreach (string ticket in CollectAllTicketsInMatch(match))
        {
            if (consumedTickets.Add(ticket))
            {
                search.Consume(ticket);
            }
        }
    }

    public List<List<MatchmakerTicket>> RunSweep(DateTime? now = null)
    {
        var activeIndexesCopy = new Dictionary<string, MatchmakerTicket>(_activeIndexes);
        var indexesCopy = new Dictionary<string, MatchmakerTicket>(_indexes);

        if (activeIndexesCopy.Count == 0)
        {
            return new();
        }

        var matched = Sweep(activeIndexesCopy, indexesCopy, now ?? DateTime.UtcNow, out var expiredTickets);

        foreach (var ticket in expiredTickets)
        {
            _activeIndexes.Remove(ticket);
        }

        foreach (var match in matched)
        {
            foreach (var matchIndex in match)
            {
                RemoveTicket(matchIndex.Ticket);
            }
        }

        return matched;
    }

    public void FlushStaleTickets()
    {
        long nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long maxAgeSeconds = _config.TicketMaxTTLInMinute * 60L;

        List<string>? stale = null;
        foreach (var (ticketId, ticket) in _indexes)
        {
            long createdAtSeconds = ticket.CreatedAt / 1_000_000_000;
            if (nowSeconds - createdAtSeconds < maxAgeSeconds)
            {
                continue;
            }

            stale ??= new List<string>();
            stale.Add(ticketId);
        }

        if (stale == null)
        {
            return;
        }

        foreach (var ticketId in stale)
        {
            CancelTicket(ticketId);
        }
    }

    public static List<MatchmakerTicket>? TryFinalizeCombo(MatchmakerTicket seed, List<MatchmakerTicket> combo)
    {
        int matchSize = combo.Sum(x => x.Size) + seed.Size;

        var possibleCombo = (matchSize % seed.CountMultiple == 0) ? combo : TrimToCountMultiple(seed, combo, matchSize);
        if (possibleCombo == null)
        {
            return null;
        }

        int finalSize = possibleCombo.Sum(x => x.Size) + seed.Size;

        if (finalSize < seed.MinCount || finalSize > seed.MaxCount)
        {
            return null;
        }


        if (!possibleCombo.All(ticket => ticket.MinCount <= finalSize && ticket.MaxCount >= finalSize && finalSize % ticket.CountMultiple == 0))
        {
            return null;
        }

        return [.. possibleCombo, seed];
    }

    public static List<MatchmakerTicket>? TrimToSeatable(MatchmakerTicket seed, List<MatchmakerTicket> combo)
    {
        var seatable = combo;
        int size = seatable.Sum(x => x.Size) + seed.Size;

        while (size >= seed.MinCount && size <= seed.MaxCount)
        {
            var unseatable = seatable
                .Where(x => x.MinCount > size || x.MaxCount < size)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefault();

            if (unseatable == null)
            {
                return seatable;
            }

            seatable = seatable.Where(x => x != unseatable).ToList();
            size -= unseatable.Size;

            if (seatable.Count == 0)
            {
                return null; // a seed with room to grow never fills its own lobby
            }
        }

        return null;
    }

    public static List<MatchmakerTicket>? TrimToCountMultiple(MatchmakerTicket seed, List<MatchmakerTicket> combo, int matchSize)
    {
        int overshoot = matchSize % seed.CountMultiple;
        if (overshoot == 0)
        {
            return combo;
        }

        var removable = FindNewestGroupSummingTo(combo, overshoot);
        if (removable == null)
        {
            return null;
        }

        var dropped = new HashSet<MatchmakerTicket>(removable);
        return combo.Where(x => !dropped.Contains(x)).ToList();
    }

    public static List<MatchmakerTicket>? FindNewestGroupSummingTo(IReadOnlyList<MatchmakerTicket> tickets, int required)
    {
        if (required <= 0)
        {
            return null;
        }

        var remainingBySuffix = new int[tickets.Count + 1];
        for (int position = tickets.Count - 1; position >= 0; position--)
        {
            remainingBySuffix[position] = remainingBySuffix[position + 1] + tickets[position].Size;
        }

        List<MatchmakerTicket>? newest = null;
        long newestCreatedAt = 0;
        var picked = new List<MatchmakerTicket>();

        void Search(int position, int remaining)
        {
            if (remaining == 0)
            {
                long createdAt = AverageCreatedAt(picked);
                if (newest == null || createdAt > newestCreatedAt)
                {
                    newest = new List<MatchmakerTicket>(picked);
                    newestCreatedAt = createdAt;
                }
                return;
            }
            if (position >= tickets.Count || remainingBySuffix[position] < remaining)
            {
                return;
            }

            MatchmakerTicket current = tickets[position];

            if (current.Size <= remaining)
            {
                picked.Add(current);
                Search(position + 1, remaining - current.Size);
                picked.RemoveAt(picked.Count - 1);
            }

            Search(position + 1, remaining);
        }

        Search(0, required);

        return newest;
    }

    private static long AverageCreatedAt(List<MatchmakerTicket> group)
    {
        long baseline = group[0].CreatedAt;
        long offsetTotal = 0;
        foreach (MatchmakerTicket ticket in group)
        {
            offsetTotal += ticket.CreatedAt - baseline;
        }

        return baseline + offsetTotal / group.Count;
    }

    private HashSet<string> CollectAllTicketsInMatch(List<MatchmakerTicket> match)
    {
        HashSet<string> consumedTickets = new();
        foreach (var ticketIndex in match)
        {
            foreach (var sessionId in ticketIndex.Members)
            {
                if (!_sessionTickets.TryGetValue(sessionId, out var memberTickets) || memberTickets.Count <= 1)
                {
                    continue;
                }

                foreach (var ticket in memberTickets)
                {
                    if (ticket == ticketIndex.Ticket) continue;
                    consumedTickets.Add(ticket);
                }
            }
            consumedTickets.Add(ticketIndex.Ticket);
        }

        return consumedTickets;
    }

    public bool CancelTicket(string ticketId)
    {
        if (!_indexes.TryGetValue(ticketId, out var index))
        {
            return false;
        }

        var cancelling = new HashSet<string> { ticketId };

        if (index.PartyId != "")
        {
            foreach (var sessionId in index.Members)
            {
                if (_sessionTickets.TryGetValue(sessionId, out var sessionTickets))
                {
                    cancelling.UnionWith(sessionTickets);
                }
            }
            _partySessions.Remove(index.PartyId);
        }

        foreach (var cancelled in cancelling)
        {
            if (_indexes.TryGetValue(cancelled, out var cancelledIndex))
            {
                RemoveIndex(cancelledIndex);
            }
        }

        foreach (var sessionId in index.Members)
        {
            if (!_sessionTickets.TryGetValue(sessionId, out var sessionTickets))
            {
                continue;
            }

            sessionTickets.ExceptWith(cancelling);
            if (sessionTickets.Count == 0)
            {
                _sessionTickets.Remove(sessionId);
            }
        }

        return true;
    }

    private void RemoveTicket(string ticketId)
    {
        if (!_indexes.TryGetValue(ticketId, out var index))
        {
            return; // Add assert shouldn't happen
        }

        RemoveIndex(index);

        foreach (var member in index.Members)
        {
            if (_sessionTickets.TryGetValue(member, out var memberTickets) && memberTickets.Count > 1)
            {
                foreach (var ticket in memberTickets)
                {
                    if (ticket != ticketId && _indexes.TryGetValue(ticket, out var i))
                    {
                        RemoveIndex(i);
                    }
                }
            }

            _sessionTickets.Remove(member);
        }

        if (index.PartyId != "")
        {
            _partySessions.Remove(index.PartyId);
        }
    }

    private bool RemoveIndex(MatchmakerTicket index)
    {
        _index.Delete(index.Ticket);
        _indexes.Remove(index.Ticket);
        _activeIndexes.Remove(index.Ticket);

        return true;
    }

    public void Dispose()
    {
        _index.Dispose();
    }
}
