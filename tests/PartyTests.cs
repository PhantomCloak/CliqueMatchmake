namespace CliqueMatchmaker.Tests;

public class PartyTests
{
    [Test]
    public void TwoPartiesFillAMatch()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "pA", members: ["p1", "p2", "p3"], ranges: [(0, 6, 6), (m.Config.MaxTicketPatienceInSec, 3, 6)]);
        Party(m, partyId: "pB", members: ["p4", "p5", "p6"], ranges: [(0, 6, 6), (m.Config.MaxTicketPatienceInSec, 3, 6)]);

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Sum(ticket => ticket.Size), Is.EqualTo(6));
        Assert.That(matches[0].Select(ticket => ticket.PartyId), Is.EquivalentTo(new[] { "pA", "pB" }));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4", "p5", "p6" }));
        Assert.That(m.PoolSize, Is.Zero);
    }

    [Test]
    public void PartyAndSolosFillAMatch()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "pA", members: ["p1", "p2", "p3"], ranges: [(0, 4, 4)]);
        Ticket(m, player: "p4", ranges: [(0, 4, 4)]);
        Ticket(m, player: "p5", ranges: [(0, 4, 4)]);

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Sum(ticket => ticket.Size), Is.EqualTo(4));

        var seated = matches[0].SelectMany(ticket => ticket.Members).ToList();
        Assert.That(seated, Does.Contain("p1").And.Contain("p2").And.Contain("p3"));
        Assert.That(seated.Count(player => player is "p4" or "p5"), Is.EqualTo(1));

        Assert.That(m.PoolSize, Is.EqualTo(1));
    }

    [Test]
    public void SamePartyTicketsDoNotMatchEachOther()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)]);
        Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)]);

        var t0 = DateTime.UtcNow;

        Assert.That(m.RunSweep(t0), Is.Empty);
        Assert.That(m.PoolSize, Is.EqualTo(2));
    }

    [Test]
    public void PartyIsNeverSplitAcrossMatches()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "pA", members: ["p1", "p2", "p3"], ranges: [(0, 4, 4)]);
        Party(m, partyId: "pB", members: ["p4", "p5", "p6"], ranges: [(0, 4, 4)]);
        Ticket(m, player: "p7", ranges: [(0, 4, 4)]);

        var t0 = DateTime.UtcNow;

        var matches = m.RunSweep(t0);

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Sum(ticket => ticket.Size), Is.EqualTo(4));

        foreach (string party in new[] { "pA", "pB" })
        {
            var seatedFromParty = matches.SelectMany(match => match).Where(ticket => ticket.PartyId == party).Sum(ticket => ticket.Size);

            Assert.That(seatedFromParty, Is.AnyOf(0, 3), $"{party} was split across the match boundary");
        }

        Assert.That(m.PoolSize, Is.EqualTo(1));
    }

    [Test]
    public void PartyRelaxesToItsMinimumOnceItsPatienceRunsOut()
    {
        const int Patience = 30;
        using var m = new Matchmaker(new MatchmakerConfig { MaxTicketPatienceInSec = Patience });

        Party(m, partyId: "pA", members: ["p1", "p2", "p3"], ranges: [(0, 8, 8), (m.Config.MaxTicketPatienceInSec, 4, 8)]);
        Party(m, partyId: "pB", members: ["p4", "p5"], ranges: [(0, 8, 8), (m.Config.MaxTicketPatienceInSec, 4, 8)]);

        var t0 = DateTime.UtcNow;

        foreach (int second in new[] { 0, 10, 29 })
        {
            Assert.That(m.RunSweep(t0.AddSeconds(second)), Is.Empty, $"t+{second}");
            Assert.That(m.ActivePoolSize, Is.EqualTo(2), $"t+{second}");
        }

        var matches = m.RunSweep(t0.AddSeconds(Patience));

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Sum(ticket => ticket.Size), Is.EqualTo(5));
        Assert.That(m.PoolSize, Is.Zero);
    }

    [Test]
    public void PartyMatchItsOwnWhenRosterSatisfiesTheMinimum()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "pA", members: ["p1", "p2", "p3", "p4"], ranges: [(0, 6, 6), (m.Config.MaxTicketPatienceInSec, 4, 6)]);

        var t0 = DateTime.UtcNow;
        var tMax = t0.AddSeconds(m.Config.MaxTicketPatienceInSec);

        Assert.That(m.RunSweep(t0), Is.Empty);
        Assert.That(m.RunSweep(tMax), Has.Count.EqualTo(1));

        using var control = new Matchmaker();

        Party(control, partyId: "pA", members: ["p1", "p2", "p3", "p4"], ranges: [(0, 2, 6)]);

        Assert.That(control.RunSweep(DateTime.UtcNow), Has.Count.EqualTo(1));
    }

    [Test]
    public void PartyAtItsCeilingIsNeverSeatedWithAnyoneElse()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "pA", members: ["p1", "p2", "p3", "p4"], ranges: [(0, 2, 4)]);
        Ticket(m, player: "p5", ranges: [(0, 2, 6)]);
        Ticket(m, player: "p6", ranges: [(0, 2, 6)]);

        var t0 = DateTime.UtcNow;
        var matches = new List<List<MatchmakerTicket>>();

        matches.AddRange(m.RunSweep(t0));
        matches.AddRange(m.RunSweep(t0.AddSeconds(m.Config.MaxTicketPatienceInSec)));

        foreach (var match in matches)
        {
            var members = match.SelectMany(ticket => ticket.Members).ToList();

            if (!match.Any(ticket => ticket.PartyId == "pA"))
            {
                continue;
            }

            Assert.That(members, Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4" }),
                "a party at its ceiling leaves no seat for anyone else to take");
        }

        Assert.That(matches.Any(match => match.SelectMany(ticket => ticket.Members).Contains("p5")),
            Is.True, "the pool is live: the solos seat each other while the party is passed over");
    }

    [Test]
    public void QueryBoostSeatsPartiesAheadOfLongerWaitingSolos()
    {
        using var m = new Matchmaker();
        const int TeamSize = 5;

        var solos = AddPartyPrioritySolos(m, count: 10, teamSize: TeamSize, boosted: true);
        AddPartyPriorityTicket(m, partyId: "pA", members: ["p11", "p12"], teamSize: TeamSize, boosted: true);
        AddPartyPriorityTicket(m, partyId: "pB", members: ["p13", "p14", "p15"], teamSize: TeamSize, boosted: true);

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));

        var match = matches[0];
        Assert.That(match.Sum(ticket => ticket.Size), Is.EqualTo(TeamSize * 2));

        Assert.That(match.Where(ticket => ticket.PartyId != "").Select(ticket => ticket.PartyId), Is.EquivalentTo(new[] { "pA", "pB" }));

        Assert.That(match.SelectMany(ticket => ticket.Members), Is.EquivalentTo(solos.Take(5).Concat(new[] { "p11", "p12", "p13", "p14", "p15" })));

        Assert.That(m.PoolSize, Is.EqualTo(5), "p6..p10 queued before either party and should have lost their seats to it");
        Assert.That(m.ActivePoolSize, Is.Zero);
    }

    [Test]
    public void FullPartyMatchingOnItsOwnCancelsThePartysOtherTickets()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "pA", members: ["p1", "p2", "p3", "p4"], ranges: [(0, 4, 4)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        Party(m, partyId: "pA", members: ["p1", "p2", "p3", "p4"], ranges: [(0, 8, 8), (m.Config.MaxTicketPatienceInSec, 4, 8)], query: "+properties.mode:casual", properties: new() { ["mode"] = "casual" });
        Party(m, partyId: "pC", members: ["p5", "p6", "p7", "p8"], ranges: [(0, 8, 8), (m.Config.MaxTicketPatienceInSec, 4, 8)], query: "+properties.mode:casual", properties: new() { ["mode"] = "casual" });

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4" }));
        Assert.That((string)matches[0][0].Properties["mode"], Is.EqualTo("ranked"));

        Assert.That(m.PoolSize, Is.EqualTo(1));

        Party(m, partyId: "pN", members: ["p9", "p10", "p11", "p12"], ranges: [(0, 8, 8), (m.Config.MaxTicketPatienceInSec, 4, 8)], query: "+properties.mode:casual", properties: new() { ["mode"] = "casual" });

        var secondWave = m.RunSweep();

        Assert.That(secondWave, Has.Count.EqualTo(1));
        Assert.That(secondWave[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p5", "p6", "p7", "p8", "p9", "p10", "p11", "p12" }));
        Assert.That(m.PoolSize, Is.Zero);
    }

    [Test]
    public void CancellingASoloTicketLeavesTheirOtherTicketsQueued()
    {
        using var m = new Matchmaker();

        string ranked = Ticket(m, player: "p1", ranges: [(0, 2, 2)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }).Ticket;
        Ticket(m, player: "p1", ranges: [(0, 2, 2)], query: "+properties.mode:casual", properties: new() { ["mode"] = "casual" });
        Ticket(m, player: "p1", ranges: [(0, 2, 2)], query: "+properties.mode:arcade", properties: new() { ["mode"] = "arcade" });

        Assert.That(m.PoolSize, Is.EqualTo(3));
        Assert.That(m.CancelTicket(ranked), Is.True);
        Assert.That(m.PoolSize, Is.EqualTo(2));

        Ticket(m, player: "p2", ranges: [(0, 2, 2)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        Assert.That(m.RunSweep(), Is.Empty);

        Ticket(m, player: "p3", ranges: [(0, 2, 2)], query: "+properties.mode:casual", properties: new() { ["mode"] = "casual" });

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p3" }));
    }

    [Test]
    public void PartyMatchCancelsThePartysOtherQueuedTickets()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)], query: "+properties.mode:casual", properties: new() { ["mode"] = "casual" });

        Party(m, partyId: "pR", members: ["p3", "p4"], ranges: [(0, 4, 4)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        Party(m, partyId: "pC", members: ["p5", "p6"], ranges: [(0, 4, 4)], query: "+properties.mode:casual", properties: new() { ["mode"] = "casual" });

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4" }));
        Assert.That(matches[0].Select(ticket => (string)ticket.Properties["mode"]).Distinct().Single(), Is.EqualTo("ranked"));

        Assert.That(m.PoolSize, Is.EqualTo(1));

        Party(m, partyId: "pC2", members: ["p7", "p8"], ranges: [(0, 4, 4)], query: "+properties.mode:casual", properties: new() { ["mode"] = "casual" });

        var secondWave = m.RunSweep();

        Assert.That(secondWave, Has.Count.EqualTo(1));
        Assert.That(secondWave[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p5", "p6", "p7", "p8" }));
        Assert.That(m.PoolSize, Is.Zero);

        // being seated releases the party's sessions, so pA can queue again at once
        Assert.DoesNotThrow(() => Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)], query: "+properties.mode:arcade", properties: new() { ["mode"] = "arcade" }));
        Assert.That(m.PoolSize, Is.EqualTo(1));
    }

    [Test]
    public void CancellingOnePartyTicketCancelsThemAll()
    {
        using var m = new Matchmaker();

        string ranked = Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }).Ticket;
        Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)], query: "+properties.mode:casual", properties: new() { ["mode"] = "casual" });
        Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)], query: "+properties.mode:arcade", properties: new() { ["mode"] = "arcade" });

        Party(m, partyId: "pR", members: ["p3", "p4"], ranges: [(0, 4, 4)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        Party(m, partyId: "pC", members: ["p5", "p6"], ranges: [(0, 4, 4)], query: "+properties.mode:casual", properties: new() { ["mode"] = "casual" });
        Party(m, partyId: "pX", members: ["p7", "p8"], ranges: [(0, 4, 4)], query: "+properties.mode:arcade", properties: new() { ["mode"] = "arcade" });

        Assert.That(m.PoolSize, Is.EqualTo(6));
        Assert.That(m.CancelTicket(ranked), Is.True);

        Assert.That(m.PoolSize, Is.EqualTo(3));

        var t0 = DateTime.UtcNow;

        Assert.That(m.RunSweep(t0), Is.Empty);
        Assert.That(m.PoolSize, Is.EqualTo(3));

        Assert.DoesNotThrow(() => Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" }));
    }

    [Test]
    public void PartyIdCannotBeReusedWithADifferentRoster()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)]);

        Assert.Throws<MatchmakerException>(() => Party(m, partyId: "pA", members: ["p3", "p4"], ranges: [(0, 4, 4)]));

        Assert.That(m.PoolSize, Is.EqualTo(1), "a rejected Add leaves no partial state");

        Assert.Throws<MatchmakerException>(() => Party(m, partyId: "pA", members: ["p1", "p2", "p3"], ranges: [(0, 4, 4)]));
        Assert.Throws<MatchmakerException>(() => Party(m, partyId: "pA", members: ["p1"], ranges: [(0, 4, 4)]));

        Assert.That(m.PoolSize, Is.EqualTo(1));

        Assert.DoesNotThrow(() => Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)]));
        Assert.That(m.PoolSize, Is.EqualTo(2));
    }

    [Test]
    public void SessionCannotBeInTwoPartiesAtOnce()
    {
        using var m = new Matchmaker();

        Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)]);

        var ex = Assert.Throws<MatchmakerException>(() => Party(m, partyId: "pB", members: ["p1", "p3"], ranges: [(0, 4, 4)]));

        Assert.That(ex!.Message, Is.EqualTo(MatchmakerException.InvalidPartyId));
    }

    [Test]
    public void PartyIsCappedByMaxTicketPerSession()
    {
        using var m = new Matchmaker(new MatchmakerConfig { MaxTicketPerSession = 3 });

        foreach (string mode in new[] { "ranked", "casual", "arcade" })
        {
            Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)], query: $"+properties.mode:{mode}", properties: new() { ["mode"] = mode });
        }

        Assert.Throws<MatchmakerException>(() => Party(m, partyId: "pA", members: ["p1", "p2"], ranges: [(0, 4, 4)], query: "+properties.mode:brawl", properties: new() { ["mode"] = "brawl" }));

        Assert.That(m.PoolSize, Is.EqualTo(3));
    }

    [Test]
    public void PartyLargerThanItsMaxCountIsRejected()
    {
        using var m = new Matchmaker();

        Assert.Throws<ArgumentException>(() => Party(m, partyId: "pA", members: ["p1", "p2", "p3", "p4", "p5", "p6"], ranges: [(0, 4, 4), (m.Config.MaxTicketPatienceInSec, 2, 4)]));

        Assert.That(m.PoolSize, Is.Zero);

        Assert.DoesNotThrow(() => Party(m, partyId: "pA", members: ["p1", "p2", "p3", "p4"], ranges: [(0, 6, 6), (m.Config.MaxTicketPatienceInSec, 4, 6)]));
    }

    [Test]
    public void MultiSessionTicketRequiresAPartyId()
    {
        using var m = new Matchmaker();

        Assert.Throws<ArgumentException>(() => m.Add(
            sessionIds: ["p1", "p2"],
            ownerSessionId: "p1",
            partyId: "",
            query: "+properties.mode:ranked",
            properties: new() { ["mode"] = "ranked" },
            minMaxLadder: [(0, 4, 4)]));

        Assert.That(m.PoolSize, Is.Zero);
    }

    const int PartyPriorityBoost = 10;
    const int PartyPriorityMmr = 2500;
    const int PartyPriorityTolerance = 500;

    static string PartyPriorityQuery(bool boosted) =>
        $"+properties.mode:ranked +properties.skill:[{PartyPriorityMmr - PartyPriorityTolerance} TO {PartyPriorityMmr + PartyPriorityTolerance}]" + (boosted ? $" properties.party:T^{PartyPriorityBoost}" : "");

    static void AddPartyPriorityTicket(Matchmaker matchmaker, string partyId, string[] members, int teamSize, bool boosted) =>
        matchmaker.Add(
            sessionIds: [.. members],
            ownerSessionId: members[0],
            partyId: partyId,
            query: PartyPriorityQuery(boosted),
            properties: new()
            {
                ["mode"] = "ranked",
                ["skill"] = PartyPriorityMmr,
                ["party"] = partyId != "",
            },
            minMaxLadder: [(0, teamSize * 2, teamSize * 2)]);

    static List<string> AddPartyPrioritySolos(Matchmaker matchmaker, int count, int teamSize, bool boosted)
    {
        var players = new List<string>(count);

        for (int i = 1; i <= count; i++)
        {
            string player = $"p{i}";
            players.Add(player);

            AddPartyPriorityTicket(matchmaker, partyId: "", members: [player], teamSize: teamSize, boosted: boosted);
        }

        return players;
    }
}
