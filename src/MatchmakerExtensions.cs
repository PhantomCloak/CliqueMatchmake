namespace CliqueMatchmaker;

using Lucene.Net.Index;
using Lucene.Net.Search;

using QueryRung = (int AtSec, string Query);
using MinMaxRung = (int AtSec, int Min, int Max);

public partial class Matchmaker
{
    static readonly Term BackfillTerm = new("properties.backfill", "T");
    const string BackfillClause = "-properties.backfill:T";

    public (string Ticket, long CreatedAt) AddBackfill(HashSet<string> sessionIds, string ownerSessionId, string partyId, QueryRung[] queryLadder, Dictionary<string, object> properties, MinMaxRung[] minMaxLadder, int countMultiple = 1, DateTime? createdAt = null)
    {
        if (sessionIds.Any(_sessionTickets.ContainsKey))
        {
            throw new MatchmakerException(MatchmakerException.BackfillRosterQueued);
        }

        for (int i = 0; i < minMaxLadder.Length; i++)
        {
            if (minMaxLadder[i].Min <= sessionIds.Count)
            {
                throw new ArgumentException(
                    $"Invalid minimum count at range rung {i}, must be > the roster ({sessionIds.Count}): a backfill seated at its own size comes back with no one new",
                    nameof(minMaxLadder));
            }
        }

        var backfillRanges = WithSearchingRung(minMaxLadder, _config.MaxTicketPatienceInSec);

        if (backfillRanges.Length > _maxLadderRungs)
        {
            throw new ArgumentException(
                $"Invalid range ladder, it leaves no room for the rung at {_config.MaxTicketPatienceInSec} that keeps a backfill searching, and MaxLadderRungs is {_config.MaxLadderRungs}",
                nameof(minMaxLadder));
        }

        var backfillLadder = new QueryRung[queryLadder.Length];
        for (int i = 0; i < queryLadder.Length; i++)
        {
            string query = string.IsNullOrEmpty(queryLadder[i].Query) ? "*" : queryLadder[i].Query;
            var queryRung = MatchQueryParser.ParseQueryString(query);

            if (queryRung is BooleanQuery boolean && boolean.Clauses.All(clause => clause.Occur == Occur.MUST_NOT))
            {
                throw new ArgumentException(
                    $"Invalid query at query rung {i}, must require something for {BackfillClause} to subtract from: a query of nothing but MUST_NOT matches no one",
                    nameof(queryLadder));
            }

            backfillLadder[i] = (queryLadder[i].AtSec, WithBackfillClause(query, queryRung));
        }

        return AddTicket(sessionIds, ownerSessionId, partyId, backfillLadder,
            new Dictionary<string, object>(properties, properties.Comparer) { ["backfill"] = true },
            backfillRanges, _maxLadderRungs, countMultiple, createdAt);
    }

    static MinMaxRung[] WithSearchingRung(MinMaxRung[] minMaxLadder, int patienceInSec)
    {
        if (minMaxLadder.Length == 0)
        {
            return minMaxLadder;
        }

        MinMaxRung last = minMaxLadder[^1];

        if (last.Min != last.Max || last.AtSec >= patienceInSec)
        {
            return minMaxLadder;
        }

        return [.. minMaxLadder, (patienceInSec, last.Min, last.Max)];
    }

    static string WithBackfillClause(string query, Query parsed) => parsed switch
    {
        BooleanQuery boolean when boolean.Clauses.Any(clause =>
            clause.Occur == Occur.MUST_NOT && clause.Query is TermQuery term && term.Term.Equals(BackfillTerm)) => query,
        MatchAllDocsQuery => $"+*:* {BackfillClause}",
        _ => $"{query} {BackfillClause}",
    };
}
