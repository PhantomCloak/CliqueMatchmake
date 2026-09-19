namespace Sukhoi;

using Lucene.Net.Index;
using Lucene.Net.Search;

using QueryRung = (int AtSec, string Query);
using MinMaxRung = (int AtSec, int Min, int Max);

public partial class Matchmaker
{
    public (string Ticket, long CreatedAt) AddBackfill(HashSet<string> sessionIds, string ownerSessionId, string partyId, QueryRung[] queryLadder, Dictionary<string, object> properties, MinMaxRung[] minMaxLadder, int countMultiple = 1, DateTime? createdAt = null)
    {
        if (sessionIds.Any(_sessionTickets.ContainsKey))
        {
            throw new MatchmakerException(MatchmakerException.BackfillRosterQueued);
        }

        if (!properties.TryGetValue("backfill", out object? marker) || marker is not true)
        {
            throw new ArgumentException("Invalid backfill properties, must carry backfill = true", nameof(properties));
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

        Term BackfillTerm = new("properties.backfill", "T");
        for (int i = 0; i < queryLadder.Length; i++)
        {
            string query = string.IsNullOrEmpty(queryLadder[i].Query) ? "*" : queryLadder[i].Query;
            var queryRung = MatchQueryParser.ParseQueryString(query);
            if (!(queryRung is BooleanQuery boolean &&
                        boolean.Any(clause => clause.Occur != Occur.MUST_NOT) &&
                        boolean.Clauses.Any(clause => clause.Occur == Occur.MUST_NOT &&
                        clause.Query is TermQuery term &&
                        term.Term.Equals(BackfillTerm))))
            {
                throw new ArgumentException(
                    $"Invalid query at query rung {i}, must require something and carry -properties.backfill:T: without it two lobbies can be seated together",
                    nameof(queryLadder));
            }
        }

        return AddTicket(sessionIds, ownerSessionId, partyId, queryLadder, properties, minMaxLadder, _maxLadderRungs, countMultiple, createdAt);
    }

}
