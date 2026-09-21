namespace CliqueMatchmaker.Tests;

public class BackfillTests
{
    const string BackfillQuery = "+properties.mode:ranked -properties.backfill:T";
    [Test]
    public void BackfillTicketFillsTheLobbyToCapacity()
    {
        using var m = new Matchmaker();

        Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });

        foreach (string player in new[] { "p8", "p9", "p10" })
        {
            Ticket(m, player: player, ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
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

        Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        Backfill(m, partyId: "lobbyB", members: ["p5", "p6", "p7", "p8"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        Ticket(m, player: "p9", ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        Ticket(m, player: "p10", ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });

        Assert.That(m.RunSweep(), Is.Empty, "the two lobbies are the only way to reach 10, and each turns the other away");
        Assert.That(m.PoolSize, Is.EqualTo(4));

        using var control = new Matchmaker();

        Party(control, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked", ["backfill"] = true });
        Party(control, partyId: "lobbyB", members: ["p5", "p6", "p7", "p8"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked", ["backfill"] = true });
        Ticket(control, player: "p9", ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        Ticket(control, player: "p10", ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });

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
            Ticket(m, player: player, ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        }

        Assert.That(m.RunSweep(), Is.Empty, "a query of nothing but MUST_NOT matches no one");
        Assert.That(m.PoolSize, Is.EqualTo(4));

        using var matchAll = new Matchmaker();

        Backfill(matchAll, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+*:* -properties.backfill:T", properties: new() { ["mode"] = "ranked" });

        foreach (string player in new[] { "p8", "p9", "p10" })
        {
            Ticket(matchAll, player: player, ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
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

        Backfill(control, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10), (10, 8, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }, createdAt: t0);

        Assert.That(control.RunSweep(t0), Is.Empty);
        Assert.That(control.RunSweep(t0.AddSeconds(10)), Is.Empty, "a floor of 8 still needs someone new");
    }

   [Test]
    public void RosterChangeIsACancelAndResubmit()
    {
        using var m = new Matchmaker();

        string backfill = Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }).Ticket;

        Assert.Throws<MatchmakerException>(() => Ticket(m, player: "p7", ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }),
            "p7 is still seated on the lobby's ticket");

        Assert.That(m.CancelTicket(backfill), Is.True);

        Assert.DoesNotThrow(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }));
        Assert.DoesNotThrow(() => Ticket(m, player: "p7", ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }));

        Assert.That(m.PoolSize, Is.EqualTo(2));
    }

    [Test]
    public void AddBackfillIsCancelledWhileARosterMemberIsQueued()
    {
        using var m = new Matchmaker();

        string queued = Ticket(m, player: "p3", ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }).Ticket;

        var error = Assert.Throws<MatchmakerException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }));
        Assert.That(error!.Message, Is.EqualTo(MatchmakerException.BackfillRosterQueued));
        Assert.That(m.PoolSize, Is.EqualTo(1), "a cancelled backfill leaves no partial state");

        Assert.That(m.CancelTicket(queued), Is.True);
        Assert.DoesNotThrow(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }));

        Assert.Throws<MatchmakerException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }),
            "a second submit for the same lobby finds its roster queued on the first");
        Assert.That(m.PoolSize, Is.EqualTo(1));

        foreach (string player in new[] { "p8", "p9", "p10" })
        {
            Ticket(m, player: player, ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        }

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10" }));
        Assert.That(m.PoolSize, Is.Zero);
    }

    [Test]
    public void SeedThatCannotReachItsCeilingFillsTheLobbyInstead()
    {
        using var m = new Matchmaker();

        Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });

        Assert.That(m.RunSweep(), Is.Empty, "the lobby searches once and then waits to be picked up");

        foreach (string player in new[] { "p8", "p9", "p10", "p11", "p12", "p13" })
        {
            Ticket(m, player: player, ranges: [(0, 10, 12)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        }

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1), "p8 cannot reach the 12 it asked for, so it settles for the 10 the lobby seats");
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10" }));
        Assert.That(m.PoolSize, Is.EqualTo(3), "p11..p13 are too few to seat each other");
    }

    [Test]
    public void TicketThatCannotSitWithTheLobbyLeavesItsSeatsAlone()
    {
        using var m = new Matchmaker();

        Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10), (m.Config.MaxTicketPatienceInSec, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked", ["backfill"] = true });

        Assert.That(m.RunSweep(), Is.Empty);

        Ticket(m, player: "p8", ranges: [(0, 10, 12)], query: "+properties.mode:ranked properties.backfill:T^10", properties: new() { ["mode"] = "ranked" });
        Ticket(m, player: "p9", ranges: [(0, 12, 12)], query: "+properties.mode:ranked properties.backfill:T^10", properties: new() { ["mode"] = "ranked" });
        Ticket(m, player: "p10", ranges: [(0, 10, 12)], query: "+properties.mode:ranked properties.backfill:T^10", properties: new() { ["mode"] = "ranked" });
        foreach (string player in new[] { "p11", "p12", "p13" })
        {
            Ticket(m, player: player, ranges: [(0, 10, 12)], query: "+properties.mode:ranked properties.backfill:T^10", properties: new() { ["mode"] = "ranked" });
        }

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p10", "p11" }),
            "p9 only takes a match of 12, which the lobby can never be part of");
        Assert.That(m.PoolSize, Is.EqualTo(3), "p9 is left with p12 and p13, too few for the 12 it asked for");
    }

    [Test]
    public void AddBackfillKeepsAPinnedLobbySearchingToTheEndOfItsPatience()
    {
        var t0 = DateTime.UtcNow;

        using var m = new Matchmaker();

        Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }, createdAt: t0);

        Assert.That(m.RunSweep(t0), Is.Empty);
        Assert.That(m.ActivePoolSize, Is.EqualTo(1), "one pinned rung on its own would have retired the lobby on its first pass");

        Assert.That(m.RunSweep(t0.AddSeconds(m.Config.MaxTicketPatienceInSec - 1)), Is.Empty);
        Assert.That(m.ActivePoolSize, Is.EqualTo(1));

        Assert.That(m.RunSweep(t0.AddSeconds(m.Config.MaxTicketPatienceInSec)), Is.Empty);
        Assert.That(m.ActivePoolSize, Is.Zero, "its age retires it at the end of its patience");
    }

    [Test]
    public void LobbyStillSearchingFillsWhenTheSolosAskForMoreSeatsThanItHas()
    {
        var t0 = DateTime.UtcNow;

        using var m = new Matchmaker();

        Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }, createdAt: t0);

        Assert.That(m.RunSweep(t0), Is.Empty);

        foreach (string player in new[] { "p8", "p9", "p10", "p11", "p12", "p13" })
        {
            Ticket(m, player: player, ranges: [(0, 10, 12)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }, createdAt: t0.AddSeconds(1));
        }

        var matches = m.RunSweep(t0.AddSeconds(1));

        Assert.That(matches, Has.Count.EqualTo(1), "the lobby is still searching, and its own search stops at the 10 it asked for");
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10" }));

        var lobby = matches[0].Single(ticket => ticket.PartyId == "lobbyA");

        Assert.That(lobby.MinMaxLadder, Is.EqualTo(new MinMaxRung[] { (0, 10, 10), (m.Config.MaxTicketPatienceInSec, 10, 10) }));
        Assert.That(m.PoolSize, Is.EqualTo(3), "p11..p13 are too few to seat each other");
    }

    [Test]
    public void AddBackfillNeedsRoomForTheRungThatKeepsItSearching()
    {
        using var m = new Matchmaker(new MatchmakerConfig { MaxLadderRungs = 1 });

        Assert.Throws<ArgumentException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }));

        Assert.That(m.PoolSize, Is.Zero);
    }

    [Test]
    public void AddBackfillRefusesAMinTheRosterAlreadyMeets()
    {
        using var m = new Matchmaker();

        Assert.Throws<ArgumentException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10), (10, 7, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }));
        Assert.Throws<ArgumentException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10"], ranges: [(0, 10, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }),
            "a full lobby has no seat to ask for");

        Assert.That(m.PoolSize, Is.Zero);

        Assert.DoesNotThrow(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10), (10, 8, 10)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }));
    }

    [Test]
    public void AddBackfillRefusesAQueryWithNothingToSubtractFrom()
    {
        using var m = new Matchmaker();

        Assert.Throws<ArgumentException>(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "-properties.backfill:T", properties: new() { ["mode"] = "ranked" }));

        Assert.Throws<ArgumentException>(() => m.AddBackfill(
            sessionIds: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"],
            ownerSessionId: "p1",
            partyId: "lobbyA",
            queryLadder: [(0, "+properties.mode:ranked"), (10, "-properties.mode:casual")],
            properties: new() { ["mode"] = "ranked" },
            minMaxLadder: [(0, 10, 10)]), "a rung of nothing but MUST_NOT matches no one once the ladder reaches it");

        Assert.That(m.PoolSize, Is.Zero);

        Assert.DoesNotThrow(() => Backfill(m, partyId: "lobbyA", members: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], ranges: [(0, 10, 10)], query: "+*:*", properties: new() { ["mode"] = "ranked" }));
    }
}
