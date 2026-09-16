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
        Matchmaker m, string partyId, string[] members, MinMaxRung[] ranges, string? mode = null,
        int countMultiple = 1, DateTime? createdAt = null) =>
        m.Add(
            sessionIds: [.. members],
            ownerSessionId: members[0],
            partyId: partyId,
            query: ModeQuery(mode),
            properties: ModeProperties(mode),
            minMaxLadder: ranges,
            countMultiple: countMultiple,
            createdAt: createdAt);

    // "*" is the parser's "take anyone" (MatchQueryParser.ParseLadder), and a ticket that
    // constrains nothing carries nothing: acceptance is mutual, so a mode-less ticket is only
    // ever seated with other mode-less ones.
    static string ModeQuery(string? mode) => mode is null ? "*" : $"+properties.mode:{mode}";

    static Dictionary<string, object> ModeProperties(string? mode) =>
        mode is null ? [] : new Dictionary<string, object> { ["mode"] = mode };
}
