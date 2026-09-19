namespace Sukhoi.Tests;

static class TestKit
{
    public static (string Ticket, long CreatedAt) Ticket(
        Matchmaker m, string player, MinMaxRung[] ranges, QueryRung[]? queries = null,
        Dictionary<string, object>? properties = null, int countMultiple = 1, DateTime? createdAt = null) =>
        m.Add(
            sessionIds: [player],
            ownerSessionId: player,
            partyId: "",
            queryLadder: queries ?? [(0, "*")],
            properties: properties ?? [],
            minMaxLadder: ranges,
            countMultiple: countMultiple,
            createdAt: createdAt);

    public static (string Ticket, long CreatedAt) Ticket(
        Matchmaker m, string player, MinMaxRung[] ranges, string query,
        Dictionary<string, object>? properties = null, int countMultiple = 1, DateTime? createdAt = null) =>
        m.Add(
            sessionIds: [player],
            ownerSessionId: player,
            partyId: "",
            query: query,
            properties: properties ?? [],
            minMaxLadder: ranges,
            countMultiple: countMultiple,
            createdAt: createdAt);

    public static (string Ticket, long CreatedAt) Party(
        Matchmaker m, string partyId, string[] members, MinMaxRung[] ranges, string query = "*",
        Dictionary<string, object>? properties = null, int countMultiple = 1, DateTime? createdAt = null) =>
        m.Add(
            sessionIds: [.. members],
            ownerSessionId: members[0],
            partyId: partyId,
            query: query,
            properties: properties ?? [],
            minMaxLadder: ranges,
            countMultiple: countMultiple,
            createdAt: createdAt);

    public static (string Ticket, long CreatedAt) Backfill(
        Matchmaker m, string partyId, string[] members, MinMaxRung[] ranges, string query,
        Dictionary<string, object> properties, int countMultiple = 1, DateTime? createdAt = null) =>
        m.AddBackfill(
            sessionIds: [.. members],
            ownerSessionId: members[0],
            partyId: partyId,
            query: query,
            properties: properties,
            minMaxLadder: ranges,
            countMultiple: countMultiple,
            createdAt: createdAt);
}
