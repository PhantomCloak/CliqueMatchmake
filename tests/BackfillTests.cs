namespace Sukhoi.Tests;

// a game server backfills a running lobby by queueing its members as one party ticket that marks
// itself backfill and turns other backfills away, and asking for the lobby's full size
public class BackfillTests
{
    const string BackfillQuery = "+properties.mode:ranked -properties.backfill:T";
    const string SoloQuery = "+properties.mode:ranked";
    const string BoostedSoloQuery = SoloQuery + " properties.backfill:T^10";

    [Test]
    public void BackfillTicketFillsTheLobbyToCapacity()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true });

        foreach (string player in new[] { "p8", "p9", "p10" })
        {
            Ticket(m, player: player, ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" });
        }

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10" }));
        Assert.That(matches[0].Count(ticket => ticket.PartyId == "lobbyA"), Is.EqualTo(1));
        Assert.That(m.PoolSize, Is.Zero);
    }

    [Test]
    public void TwoBackfillTicketsNeverShareALobby()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true });
        Party(m, partyId: "lobbyB", members: ["p5", "p6", "p7", "p8"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true });
        Ticket(m, player: "p9", ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" });
        Ticket(m, player: "p10", ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" });

        Assert.That(m.RunSweep(), Is.Empty, "the two lobbies are the only way to reach 10, and each turns the other away");
        Assert.That(m.PoolSize, Is.EqualTo(4));

        using var control = new Matchmaker();

        Party(control, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4"], ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true });
        Party(control, partyId: "lobbyB", members: ["p5", "p6", "p7", "p8"], ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true });
        Ticket(control, player: "p9", ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" });
        Ticket(control, player: "p10", ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" });

        var controlMatches = control.RunSweep();

        Assert.That(controlMatches, Has.Count.EqualTo(1), "without the MUST_NOT the two lobbies are merged into one");
        Assert.That(controlMatches[0].Select(ticket => ticket.PartyId).Where(partyId => partyId != ""), Is.EquivalentTo(new[] { "lobbyA", "lobbyB" }));
    }

    [Test]
    public void NegativeOnlyBackfillQueryNeverFills()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "-properties.backfill:T", properties: new() { ["mode"] = "ranked", ["backfill"] = true });

        foreach (string player in new[] { "p8", "p9", "p10" })
        {
            Ticket(m, player: player, ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" });
        }

        Assert.That(m.RunSweep(), Is.Empty, "a query of nothing but MUST_NOT matches no one");
        Assert.That(m.PoolSize, Is.EqualTo(4));

        using var matchAll = new Matchmaker();

        Party(matchAll, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+*:* -properties.backfill:T", properties: new() { ["mode"] = "ranked", ["backfill"] = true });

        foreach (string player in new[] { "p8", "p9", "p10" })
        {
            Ticket(matchAll, player: player, ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" });
        }

        Assert.That(matchAll.RunSweep(), Has.Count.EqualTo(1), "a match-all clause gives the MUST_NOT something to subtract from");
        Assert.That(matchAll.PoolSize, Is.Zero);
    }

    [Test]
    public void BackfillWhoseMinReachesTheLobbySizeIsSeatedWithNoOneNew()
    {
        var t0 = DateTime.UtcNow;

        using var m = new Matchmaker();

        Party(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10), (10, 7, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true }, createdAt: t0);

        Assert.That(m.RunSweep(t0), Is.Empty);

        var matches = m.RunSweep(t0.AddSeconds(10));

        Assert.That(matches, Has.Count.EqualTo(1), "a floor of 7 is met by the lobby's own 7 members");
        Assert.That(matches[0].Select(ticket => ticket.PartyId), Is.EquivalentTo(new[] { "lobbyA" }));

        using var control = new Matchmaker();

        Party(control, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10), (10, 8, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true }, createdAt: t0);

        Assert.That(control.RunSweep(t0), Is.Empty);
        Assert.That(control.RunSweep(t0.AddSeconds(10)), Is.Empty, "a floor of 8 still needs someone new");
    }

    [Test]
    public void QueryBoostFillsTheBackfillAheadOfAFreshLobby()
    {
        string[] solos = ["p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10"];
        string[] lobby = ["p11", "p12", "p13", "p14", "p15", "p16", "p17"];

        using var m = new Matchmaker();

        foreach (string player in solos)
        {
            Ticket(m, player: player, ranges: [(0, 10, 10)], query: BoostedSoloQuery, properties: new() { ["mode"] = "ranked" });
        }
        Party(m, partyId: "lobbyA", members: lobby, ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true });

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(solos.Take(3).Concat(lobby)));
        Assert.That(m.PoolSize, Is.EqualTo(7), "p4..p10 queued before the lobby and should have lost their seats to it");

        using var control = new Matchmaker();

        foreach (string player in solos)
        {
            Ticket(control, player: player, ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" });
        }
        Party(control, partyId: "lobbyA", members: lobby, ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true });

        var controlMatches = control.RunSweep();

        Assert.That(controlMatches, Has.Count.EqualTo(1));
        Assert.That(controlMatches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(solos), "without the boost the solos queued first start a lobby of their own");
        Assert.That(control.PoolSize, Is.EqualTo(1), "the backfill is left waiting");
    }

    [Test]
    public void RosterChangeIsACancelAndResubmit()
    {
        using var m = new Matchmaker();

        string backfill = Party(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true }).Ticket;

        Assert.Throws<MatchmakerException>(() => Ticket(m, player: "p7", ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" }),
            "p7 is still seated on the lobby's ticket");

        Assert.That(m.CancelTicket(backfill), Is.True);

        Assert.DoesNotThrow(() => Party(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true }));
        Assert.DoesNotThrow(() => Ticket(m, player: "p7", ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" }));

        Assert.That(m.PoolSize, Is.EqualTo(2));
    }

    [Test]
    public void AddBackfillIsCancelledWhileARosterMemberIsQueued()
    {
        using var m = new Matchmaker();

        string queued = Ticket(m, player: "p3", ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" }).Ticket;

        var error = Assert.Throws<MatchmakerException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true }));
        Assert.That(error!.Message, Is.EqualTo(MatchmakerException.BackfillRosterQueued));
        Assert.That(m.PoolSize, Is.EqualTo(1), "a cancelled backfill leaves no partial state");

        Assert.That(m.CancelTicket(queued), Is.True);
        Assert.DoesNotThrow(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true }));

        Assert.Throws<MatchmakerException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true }),
            "a second submit for the same lobby finds its roster queued on the first");
        Assert.That(m.PoolSize, Is.EqualTo(1));

        foreach (string player in new[] { "p8", "p9", "p10" })
        {
            Ticket(m, player: player, ranges: [(0, 10, 10)], query: SoloQuery, properties: new() { ["mode"] = "ranked" });
        }

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10" }));
        Assert.That(m.PoolSize, Is.Zero);
    }

    [Test]
    public void AddBackfillRefusesAMinTheRosterAlreadyMeets()
    {
        using var m = new Matchmaker();

        Assert.Throws<ArgumentException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10), (10, 7, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true }));
        Assert.Throws<ArgumentException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true }),
            "a full lobby has no seat to ask for");

        Assert.That(m.PoolSize, Is.Zero);

        Assert.DoesNotThrow(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10), (10, 8, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = true }));
    }

    [Test]
    public void AddBackfillRefusesAQueryThatLetsAnotherBackfillIn()
    {
        using var m = new Matchmaker();

        foreach (string query in new[] { SoloQuery, "*", "-properties.backfill:T", "+properties.mode:ranked -properties.backfill:true" })
        {
            Assert.Throws<ArgumentException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: query, properties: new() { ["mode"] = "ranked", ["backfill"] = true }), query);
        }

        Assert.Throws<ArgumentException>(() => m.AddBackfill(
            sessionIds: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"],
            ownerSessionId: "p1",
            partyId: "lobbyA",
            queryLadder: [(0, BackfillQuery), (10, "*")],
            properties: new() { ["mode"] = "ranked", ["backfill"] = true },
            minMaxLadder: [(0, 10, 10)]), "a match-all rung drops the MUST_NOT once the ladder reaches it");

        Assert.That(m.PoolSize, Is.Zero);

        Assert.DoesNotThrow(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+*:* -properties.backfill:T", properties: new() { ["mode"] = "ranked", ["backfill"] = true }));
    }

    [Test]
    public void AddBackfillRequiresTheBackfillMarker()
    {
        using var m = new Matchmaker();

        Assert.Throws<ArgumentException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked" }));
        Assert.Throws<ArgumentException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: BackfillQuery, properties: new() { ["mode"] = "ranked", ["backfill"] = false }));

        Assert.That(m.PoolSize, Is.Zero);
    }
}
