namespace Sukhoi.Tests;

public class MatchmakerTests
{
    // Case: a conjunctive query segments the pool on every term at once - a ticket differing in a
    // single term is excluded as firmly as one differing in all of them. GameModeTests covers this
    // shape for numeric ranges; this is the string-term side of it.
    [Test]
    public void ConjunctiveQueryExcludesTicketsDifferingInASingleTerm()
    {
        using var m = new Matchmaker();

        Ticket(m, player: "wrong-region", ranges: [(AtSec: 0, Min: 2, Max: 2)],
            "+properties.mode:casual +properties.region:us-east +properties.platform:pc", properties: new() { ["mode"] = "casual", ["region"] = "us-east", ["platform"] = "pc" });
        Ticket(m, player: "wrong-platform", ranges: [(AtSec: 0, Min: 2, Max: 2)],
            "+properties.mode:casual +properties.region:eu-west +properties.platform:console", properties: new() { ["mode"] = "casual", ["region"] = "eu-west", ["platform"] = "console" });
        Ticket(m, player: "wrong-mode", ranges: [(AtSec: 0, Min: 2, Max: 2)],
            "+properties.mode:ranked +properties.region:eu-west +properties.platform:pc", properties: new() { ["mode"] = "ranked", ["region"] = "eu-west", ["platform"] = "pc" });

        Ticket(m, player: "casual1", ranges: [(AtSec: 0, Min: 2, Max: 2)],
            "+properties.mode:casual +properties.region:eu-west +properties.platform:pc", properties: new() { ["mode"] = "casual", ["region"] = "eu-west", ["platform"] = "pc" });
        Ticket(m, player: "casual2", ranges: [(AtSec: 0, Min: 2, Max: 2)],
            "+properties.mode:casual +properties.region:eu-west +properties.platform:pc", properties: new() { ["mode"] = "casual", ["region"] = "eu-west", ["platform"] = "pc" });

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));

        Assert.Multiple(() =>
        {
            Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "casual1", "casual2" }));
            Assert.That(m.PoolSize, Is.EqualTo(3), "every near-miss ticket stays queued");
        });
    }

    [Test]
    public void SettledSizeMeetsTheHighestFloorInTheLobby() // OK
    {
        using var m = new Matchmaker();

        Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)]);
        Ticket(m, player: "p2", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)]);
        Ticket(m, player: "p3", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 3, Max: 6)]);
        Ticket(m, player: "p4", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 4, Max: 6)]);

        var tMax = DateTime.UtcNow.AddSeconds(m.Config.MaxTicketPatienceInSec);

        var matches = m.RunSweep(tMax);

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Sum(ticket => ticket.Size), Is.EqualTo(4));
        Assert.That(m.PoolSize, Is.Zero);
    }

    [Test]
    public void OldestTicketsAreMatchedFirstAndTheNewestWaits()
    {
        using var m = new Matchmaker();

        long previous = 0;
        foreach (string player in new[] { "p1", "p2", "p3", "p4", "p5" })
        {
            var (_, createdAt) = Ticket(m, player: player, ranges: [(AtSec: 0, Min: 2, Max: 2)]);
            Assert.That(createdAt, Is.GreaterThan(previous), "each ticket gets a later stamp than the last");
            previous = createdAt;
        }

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(2));

        Assert.Multiple(() =>
        {
            Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2" }));
            Assert.That(matches[1].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p3", "p4" }));
            Assert.That(m.PoolSize, Is.EqualTo(1), "only the newest ticket is left waiting");
        });
    }

    [Test]
    public void EmptyQueryTicketAcceptsAnyoneButStillHasToBeAccepted()
    {
        using var m = new Matchmaker();

        Ticket(m, player: "p_any", ranges: [(AtSec: 0, Min: 2, Max: 2)], "", properties: new() { ["mode"] = "ranked" });
        Ticket(m, player: "p_picky", ranges: [(AtSec: 0, Min: 2, Max: 2)], "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));

        var match = matches[0];

        Assert.That(match.SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p_any", "p_picky" }));
        Assert.That(match.Single(ticket => ticket.Members.Contains("p_any")).QueryString, Is.EqualTo("*"));
    }

    [Test]
    public void TicketsHaveToMutuallyAcceptEachotherInLobby()
    {
        using var nonMutual = new Matchmaker();

		// Cannot satisfy match of three people because p3 does not accept p1 even p1 accepts the p3
        Ticket(nonMutual, player: "p1", ranges: [(AtSec: 0, Min: 3, Max: 3)], "+properties.skill:[1000 TO 3000]", properties: new() { ["skill"] = 1500 });
        Ticket(nonMutual, player: "p2", ranges: [(AtSec: 0, Min: 3, Max: 3)], "+properties.skill:[1500 TO 3000]", properties: new() { ["skill"] = 2000 });
        Ticket(nonMutual, player: "p3", ranges: [(AtSec: 0, Min: 3, Max: 3)], "+properties.skill:[2000 TO 3000]", properties: new() { ["skill"] = 2500 });

        var tMax = DateTime.UtcNow.AddSeconds(nonMutual.Config.MaxTicketPatienceInSec);

        Assert.That(nonMutual.RunSweep(tMax), Is.Empty);
        Assert.That(nonMutual.PoolSize, Is.EqualTo(3));

        using var mutual = new Matchmaker();

		// Everyone satisfied with each other
        Ticket(mutual, player: "p1", ranges: [(AtSec: 0, Min: 3, Max: 3)], "+properties.skill:[1000 TO 2500]", properties: new() { ["skill"] = 1500 });
        Ticket(mutual, player: "p2", ranges: [(AtSec: 0, Min: 3, Max: 3)], "+properties.skill:[1500 TO 3000]", properties: new() { ["skill"] = 2000 });
        Ticket(mutual, player: "p3", ranges: [(AtSec: 0, Min: 3, Max: 3)], "+properties.skill:[1000 TO 3000]", properties: new() { ["skill"] = 2500 });

        var matches = mutual.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3" }));
        Assert.That(mutual.PoolSize, Is.Zero);

		// Match of two people formed because settling allows to do so
        using var settles = new Matchmaker();
		int settlesTMax = settles.Config.MaxTicketPatienceInSec;

        Ticket(settles, player: "p1", ranges: [(AtSec: 0, Min: 3, Max: 3), (AtSec: settlesTMax, Min: 2, Max: 3)], "+properties.skill:[1000 TO 3000]", properties: new() { ["skill"] = 1500 });
        Ticket(settles, player: "p2", ranges: [(AtSec: 0, Min: 3, Max: 3), (AtSec: settlesTMax, Min: 2, Max: 3)], "+properties.skill:[1500 TO 3000]", properties: new() { ["skill"] = 2000 });
        Ticket(settles, player: "p3", ranges: [(AtSec: 0, Min: 3, Max: 3), (AtSec: settlesTMax, Min: 2, Max: 3)], "+properties.skill:[2000 TO 3000]", properties: new() { ["skill"] = 2500 });

        var settled = settles.RunSweep(DateTime.UtcNow.AddSeconds(settlesTMax));

        Assert.That(settled, Has.Count.EqualTo(1));
        Assert.That(settled[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2" }));
        Assert.That(settles.PoolSize, Is.EqualTo(1), "only the picky player is left queued");
    }

    [Test]
    public void PassiveFlexTicketIsMatchedByNarrowTicketsLaterOn()
    {
        using var m = new Matchmaker();

		// p1 will be flexable after patience runs out
        Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 8, Max: 8), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 8)]);

        var t0 = DateTime.UtcNow;
        var tMax = t0.AddSeconds(m.Config.MaxTicketPatienceInSec);

        Assert.That(m.RunSweep(t0), Is.Empty);
        Assert.That(m.RunSweep(tMax), Is.Empty);

        Assert.That(m.ActivePoolSize, Is.Zero);
        Assert.That(m.PoolSize, Is.EqualTo(1));

		// Narrow tickets arrives and picks up p1 since it should be relaxed by now
        foreach (string player in new[] { "p2", "p3", "p4" })
        {
            Ticket(m, player: player, ranges: [(AtSec: 0, Min: 4, Max: 4)], createdAt: tMax);
        }

        var matches = m.RunSweep(tMax);

        Assert.That(matches, Has.Count.EqualTo(1));

        var match = matches[0];

        Assert.Multiple(() =>
        {
            Assert.That(match.Sum(ticket => ticket.Size), Is.EqualTo(4));
            Assert.That(match.SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4" }));
            Assert.That(m.PoolSize, Is.Zero);
        });
    }

    [Test]
    public void SpentSeedLeavesANewcomerItsOwnPatience() // OK
    {
        // Same Ceiling
        using var sameCeiling = new Matchmaker();

        var t0 = DateTime.UtcNow;
        var tMax = t0.AddSeconds(sameCeiling.Config.MaxTicketPatienceInSec);

        Ticket(sameCeiling, player: "p1_t0", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: sameCeiling.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)], createdAt: t0);

        Assert.That(sameCeiling.RunSweep(t0), Is.Empty);

        Ticket(sameCeiling, player: "p2_tMax", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: sameCeiling.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)], createdAt: tMax);

        Assert.That(sameCeiling.RunSweep(tMax), Is.Empty, "p2_tMax has its whole patience to find a lobby of 4");
        Assert.That(sameCeiling.PoolSize, Is.EqualTo(2));

        // Wider Newcomer
        using var widerNewcomer = new Matchmaker();

        Ticket(widerNewcomer, player: "p1_t0", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: widerNewcomer.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)], createdAt: t0);

        Assert.That(widerNewcomer.RunSweep(t0), Is.Empty);

        Ticket(widerNewcomer, player: "p2_tMax", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: widerNewcomer.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)], createdAt: tMax);

        Assert.That(widerNewcomer.RunSweep(tMax), Is.Empty, "the seed cannot spend a wider ticket's patience");
        Assert.That(widerNewcomer.ActivePoolSize, Is.EqualTo(1), "only p2 is still seeding");
    }

    // Five solos cannot fill the six they packed for, so the seed settles - and the settled size
    // still has to land on the count multiple. TrimToCountMultiple gives up the newest group
    // summing to the overshoot, so the longest waiting keep their seats. The arithmetic is the
    // whole of the difference between the rows: an overshoot of one costs one ticket, an overshoot
    // of two costs two.
    [TestCase(2, 4, 4)]
    [TestCase(3, 3, 3)]
    public void LobbySettlesOnTheMultipleBelowAndDropsTheNewestTickets(int countMultiple, int floor, int expectedSize)
    {
        using var m = new Matchmaker();

        string[] players = ["p1", "p2", "p3", "p4", "p5"];

        foreach (string player in players)
        {
            Ticket(m, player: player, ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: floor, Max: 6)], countMultiple: countMultiple);
        }

        var t0 = DateTime.UtcNow;

        Assert.That(m.RunSweep(t0), Is.Empty, "rung 0 asks for exactly 6");

        var matches = m.RunSweep(t0.AddSeconds(m.Config.MaxTicketPatienceInSec));

        Assert.That(matches, Has.Count.EqualTo(1));

        Assert.Multiple(() =>
        {
            Assert.That(matches[0].Sum(ticket => ticket.Size), Is.EqualTo(expectedSize));
            Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(players.Take(expectedSize)),
                "the longest waiting keep their seats");
            Assert.That(m.PoolSize, Is.EqualTo(players.Length - expectedSize), "the newest are left waiting");
        });
    }

    [Test]
    public void TrimDropsTheNewestPartyWholeRatherThanSplittingIt() // OK
    {
        using var m = new Matchmaker();

        Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 8, Max: 8), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 4, Max: 8)], countMultiple: 4);
        Party(m, "pA", ["p2", "p3"], [(0, 8, 8), (m.Config.MaxTicketPatienceInSec, 4, 8)], countMultiple: 4);
        Party(m, "pB", ["p4", "p5"], [(0, 8, 8), (m.Config.MaxTicketPatienceInSec, 4, 8)], countMultiple: 4);
        Ticket(m, player: "p6", ranges: [(AtSec: 0, Min: 8, Max: 8), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 4, Max: 8)], countMultiple: 4);

        var tMax = DateTime.UtcNow.AddSeconds(m.Config.MaxTicketPatienceInSec);

        var matches = m.RunSweep(tMax);

        Assert.That(matches, Has.Count.EqualTo(1));

        var match = matches[0];

        Assert.Multiple(() =>
        {
            Assert.That(match.Sum(ticket => ticket.Size), Is.EqualTo(4));
            Assert.That(match.SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p6" }));
            Assert.That(m.PoolSize, Is.EqualTo(1), "pB is left queued whole");
        });
    }

    [Test]
    public void SeedSkipsADifferentCountMultipleRatherThanBlendingItIntoTheLobby() // OK
    {
        using var m = new Matchmaker();

        Ticket(m, player: "odd", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 3, Max: 6)], countMultiple: 3);
        foreach (string player in new[] { "p1", "p2", "p3", "p4" })
        {
            Ticket(m, player: player, ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)]);
        }

        var tMax = DateTime.UtcNow.AddSeconds(m.Config.MaxTicketPatienceInSec);

        var matches = m.RunSweep(tMax);

        Assert.That(matches, Has.Count.EqualTo(1));

        var match = matches[0];

        Assert.Multiple(() =>
        {
            Assert.That(match.Select(ticket => ticket.CountMultiple).Distinct(), Has.Exactly(1).Items, "a lobby is seated at one multiple");
            Assert.That(match.SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4" }));
            Assert.That(m.PoolSize, Is.EqualTo(1), "odd is left waiting");
        });
    }

    [Test]
    public void CandidateWhoseFloorTheTrimWouldUndercutIsDroppedBeforeTheTrim() // OK
    {
        using var m = new Matchmaker();

        Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)], countMultiple: 2);
        Party(m, "pA", ["p2", "p3", "p4"], [(0, 6, 6)], countMultiple: 2);
        Ticket(m, player: "p5", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)], countMultiple: 2);

        var tMax = DateTime.UtcNow.AddSeconds(m.Config.MaxTicketPatienceInSec);

        var matches = m.RunSweep(tMax);

        Assert.That(matches, Has.Count.EqualTo(1));

        Assert.Multiple(() =>
        {
            Assert.That(matches[0].Sum(ticket => ticket.Size), Is.EqualTo(2));
            Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p5" }));
            Assert.That(m.PoolSize, Is.EqualTo(1), "pA is left queued");
        });
    }

    [Test]
    public void OldestCandidateIsMatchedFirstEvenWhenANewerOneMatchesMoreOrClausesInsideParentheses() // OK
    {
        using var m = new Matchmaker();

        void AddPlayer(string player, params string[] maps) =>
            m.Add(
                sessionIds: [player],
                ownerSessionId: player,
                partyId: "",
                queryLadder: [(0, $"+({string.Join(" OR ", maps.Select(map => $"properties.map_{map}:T"))})")],
                properties: maps
                    .Select(map => new KeyValuePair<string, object>($"map_{map}", true))
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                minMaxLadder: [(0, 2, 2)]);

        AddPlayer("p1_dustOrInferno", "dust2", "inferno");
        AddPlayer("p2_dust", "dust2");
        AddPlayer("p3_dustAndInferno", "dust2", "inferno");

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1_dustOrInferno", "p2_dust" }), "the older candidate takes the seat, though p3 matches both of the seed's maps");
        Assert.That(m.PoolSize, Is.EqualTo(1), "only the newest ticket is left waiting");
    }

    [Test]
    public void MatchedOptionalClausesOutrankQueueOrderAndCreatedAtBreaksTheTie()
    {
        using var m = new Matchmaker();

        string[] PreferenceOptions = ["a", "b", "c"];

        void AddPlayer(Matchmaker matchmaker, string player, string[] wants, bool expressPreferences = true) =>
            matchmaker.Add(
                sessionIds: [player],
                ownerSessionId: player,
                partyId: "",
                queryLadder: [(0, $"+properties.mode:comp +properties.region:eu{(expressPreferences ? string.Concat(PreferenceOptions.Select(option => $" properties.opt_{option}:T")) : "")}")],
                properties: new Dictionary<string, object> { ["mode"] = "comp", ["region"] = "eu" }
                    .Concat(wants.Select(want => KeyValuePair.Create($"opt_{want}", (object)true)))
                    .ToDictionary(),
                minMaxLadder: [(0, 3, 3)]);

        AddPlayer(m, "p1", []);

        AddPlayer(m, "p2", ["a"]);
        AddPlayer(m, "p3", ["a", "b"]);
        AddPlayer(m, "p4", ["b", "c"]);
        AddPlayer(m, "p5", ["a", "b", "c"]);

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p5", "p3" }), "preference count picks the seats and age breaks the tie");
        Assert.That(m.PoolSize, Is.EqualTo(2), "the oldest candidate matched the fewest preferences");

        using var flat = new Matchmaker();

        AddPlayer(flat, "p1", [], expressPreferences: false);

        AddPlayer(flat, "p2", ["a"], expressPreferences: false);
        AddPlayer(flat, "p3", ["a", "b"], expressPreferences: false);
        AddPlayer(flat, "p4", ["b", "c"], expressPreferences: false);
        AddPlayer(flat, "p5", ["a", "b", "c"], expressPreferences: false);

        var controlMatches = flat.RunSweep();

        Assert.That(controlMatches, Has.Count.EqualTo(1));
        Assert.That(controlMatches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3" }), "with nothing to rank on, the longest waiting are seated");
    }

    [Test]
    public void FixedSizeTicketGetsOneActiveSweepWhateverItsPatienceSays() // OK
    {
        using var m = new Matchmaker();

        Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 4, Max: 4)]);

        Assert.That(m.RunSweep(), Is.Empty);
        Assert.That(m.ActivePoolSize, Is.Zero, "a fixed-size ticket has nothing to wait for");
        Assert.That(m.PoolSize, Is.EqualTo(1), "the ticket is passive, not gone");

        using var flexible = new Matchmaker();

        Ticket(flexible, player: "p2", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: flexible.Config.MaxTicketPatienceInSec, Min: 4, Max: 6)]);

        Assert.That(flexible.RunSweep(), Is.Empty);
        Assert.That(flexible.ActivePoolSize, Is.EqualTo(1));
    }

    // Case: a settling seed keeps its candidates in mutually-compatible combos, and when the first
    // of them cannot be seated it has to go on to the next. p2 is fixed at 4, so the combo it heads
    // is unseatable at the size p1 settles for; p3 heads a combo of its own only because p2 will not
    // have it. The opening sweep spends both fixed tickets' single active pass, which leaves p1 the
    // only seed at tMax - so this lobby exists at all only if the seed reaches past its first combo.
    [Test]
    public void SettlingSeedFallsThroughToItsSecondCombo()
    {
        using var m = new Matchmaker();
        int tMaxSec = m.Config.MaxTicketPatienceInSec;

        Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: tMaxSec, Min: 2, Max: 6)], "+properties.skill:[500 TO 1500]", properties: new() { ["skill"] = 1100 });
        Ticket(m, player: "p2", ranges: [(AtSec: 0, Min: 4, Max: 4)], "+properties.skill:[900 TO 1100]", properties: new() { ["skill"] = 1000 });
        Ticket(m, player: "p3", ranges: [(AtSec: 0, Min: 2, Max: 2)], "+properties.skill:[500 TO 1500]", properties: new() { ["skill"] = 1500 });

        var t0 = DateTime.UtcNow;

        Assert.That(m.RunSweep(t0), Is.Empty, "p1 asks for 6 on rung 0");
        Assert.That(m.ActivePoolSize, Is.EqualTo(1), "the fixed tickets have spent their active sweep");

        var matches = m.RunSweep(t0.AddSeconds(tMaxSec));

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p3" }));
            Assert.That(m.PoolSize, Is.EqualTo(1), "p2 is left queued");
        });
    }

    // Case: a rung is a reading off the ticket's own clock, so a ticket the pool first looks at long
    // after it queued opens on the rung its age has earned - not on rung 0, and not one rung along.
    // Every other ladder test walks a timeline from t0, which a ladder stepped per sweep would climb
    // identically; only a ticket whose very first pass is already overdue tells the two apart.
    //
    // The whole pool is placed two rungs back with createdAt rather than waiting the seconds out,
    // and neither rung below the top reaches 1500, so nothing short of the full age seats this lobby.
    [Test]
    public void TicketQueuedLongAgoStartsOnTheRungItsAgeEarns()
    {
        using var m = new Matchmaker(new MatchmakerConfig { MaxTicketPatienceInSec = 30 });

        var t0 = DateTime.UtcNow;

        Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 4, Max: 4)],
            queries: [
                (AtSec: 0, Query: "+properties.skill:[900 TO 1100]"),
                (AtSec: 10, Query: "+properties.skill:[700 TO 1300]"),
                (AtSec: 20, Query: "+properties.skill:[500 TO 2000]"),
            ],
            properties: new() { ["skill"] = 1000 }, createdAt: t0.AddSeconds(-20));

        foreach (string player in new[] { "p2", "p3", "p4" })
        {
            Ticket(m, player: player, ranges: [(AtSec: 0, Min: 4, Max: 4)], "+properties.skill:[900 TO 2000]",
                properties: new() { ["skill"] = 1500 }, createdAt: t0.AddSeconds(-20));
        }

        var matches = m.RunSweep(t0);

        Assert.That(matches, Has.Count.EqualTo(1), "the first pass reads rung 2 off the ticket's age");
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4" }));
    }

    // Case: a ladder holds its top rung for however long the ticket goes on waiting - the age keeps
    // climbing, the rung stops. Three solos can never fill the lobby of 4 they asked for, so they
    // only settle on their last interval, by which point the two-rung seed has spent its ladder two
    // slices ago and is still being matched on its top rung.
    [Test]
    public void SpentLadderHoldsItsTopRung()
    {
        using var m = new Matchmaker(new MatchmakerConfig { MaxTicketPatienceInSec = 30 });

        Ticket(m, player: "picky", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)],
            queries: [
                (AtSec: 0, Query: "+properties.mode:ranked +properties.skill:[900 TO 1100]"),
                (AtSec: 10, Query: "+properties.mode:ranked +properties.skill:[400 TO 1600]"),
            ],
            properties: new() { ["mode"] = "ranked", ["skill"] = 1000 });

        foreach (string far in new[] { "far1", "far2" })
        {
            Ticket(m, player: far, ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)],
                "+properties.mode:ranked +properties.skill:[900 TO 2100]",
                properties: new() { ["mode"] = "ranked", ["skill"] = 1500 });
        }

        var t0 = DateTime.UtcNow;

        Assert.That(m.RunSweep(t0), Is.Empty, "rung 0 does not reach the far tickets");
        Assert.That(m.RunSweep(t0.AddSeconds(10)), Is.Empty, "a lobby of 3 is short of the 4 picky packs for");
        Assert.That(m.RunSweep(t0.AddSeconds(20)), Is.Empty, "the spent ladder holds its top rung");

        var matches = m.RunSweep(t0.AddSeconds(30));

        Assert.That(matches, Has.Count.EqualTo(1), "the spent seed settles for the lobby it has");
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "picky", "far1", "far2" }));
    }

    // `pickyFirst` decides whether the relaxing ticket queues ahead of the partner it can only
    // reach from rung 1, which is the order the old bump was sensitive to.
    [TestCase(true)]
    [TestCase(false)]
    public void RungIsFixedForAWholePassWhicheverOrderThePoolQueuedIn(bool pickyFirst)
    {
        var (matches, _) = SweepInQueueOrder(
            new MatchmakerConfig { MaxTicketPatienceInSec = 10 }, pickyFirst, sweepAtSec: 10,
            first: m => Ticket(m, player: "picky", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)],
                queries: [
                    (AtSec: 0, Query: "+properties.mode:ranked +properties.skill:[900 TO 1100]"),
                    (AtSec: 10, Query: "+properties.mode:ranked +properties.skill:[400 TO 1600]"),
                ],
                properties: new() { ["mode"] = "ranked", ["skill"] = 1000 }),
            second: m => Ticket(m, player: "far1", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)],
                "+properties.mode:ranked +properties.skill:[900 TO 2100]",
                properties: new() { ["mode"] = "ranked", ["skill"] = 1500 }));

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "picky", "far1" }));
    }

    [Test]
    public void RangeLadderSettlesForTheFloorItsRungAllows()
    {
        var config = new MatchmakerConfig { MaxTicketPatienceInSec = 30 };
        using var m = new Matchmaker(config);

        Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: 10, Min: 4, Max: 6), (AtSec: 20, Min: 2, Max: 6)]);
        Ticket(m, player: "p2", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: 10, Min: 4, Max: 6), (AtSec: 20, Min: 2, Max: 6)]);

        var t0 = DateTime.UtcNow;

        Assert.That(m.RunSweep(t0), Is.Empty, "rung 0 asks for exactly 6");
        Assert.That(m.RunSweep(t0.AddSeconds(10)), Is.Empty, "rung 1's floor of 4 is still above the two present");

        var matches = m.RunSweep(t0.AddSeconds(20));

        Assert.That(matches, Has.Count.EqualTo(1), "rung 2 lowers the floor to 2");
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2" }));
        Assert.That(m.PoolSize, Is.Zero);

        using var flat = new Matchmaker(config);

        Ticket(flat, player: "p1", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: flat.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)]);
        Ticket(flat, player: "p2", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: flat.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)]);

        var flatT0 = DateTime.UtcNow;

        Assert.That(flat.RunSweep(flatT0.AddSeconds(20)), Is.Empty,
            "the same 2-6 without a ladder is not settling yet");

        var flatMatches = flat.RunSweep(flatT0.AddSeconds(30));

        Assert.That(flatMatches, Has.Count.EqualTo(1), "the flat pair waits out its whole patience");
    }

    // Case: the candidate guard is the candidate's own floor against the biggest lobby the seed could
    // build. It is the same test SearchScope prefilters on - and that is exactly why it has to be
    // repeated here: the prefilter reads the ladder's *envelope*, which for a ticket carrying a
    // schedule is the floor of its last rung, while the guard reads the rung in force this pass.
    //
    // wide is the ticket that falls in the gap. Its envelope reaches down to 2, so the prefilter
    // hands it over; its rung 0 asks for exactly 6, which narrow could never seat. Without the
    // guard it takes a seat in narrow's combo, sinks it at FinalizeCombo, and the trim cannot rescue
    // the rest - narrow is pinned to 3 at rung 0, so a lobby of 2 is below its own floor. Skipping
    // wide up front leaves the seat for fits2 and the lobby closes at 3.
    [Test]
    public void CandidateOutOfTheSeedsReachIsSkippedBeforeItTakesASeat()
    {
        using var m = new Matchmaker();

        Ticket(m, player: "narrow", ranges: [(AtSec: 0, Min: 3, Max: 3), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 3)]);
        Ticket(m, player: "wide", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)]);
        Ticket(m, player: "fits1", ranges: [(AtSec: 0, Min: 3, Max: 3), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 3)]);
        Ticket(m, player: "fits2", ranges: [(AtSec: 0, Min: 3, Max: 3), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 3)]);

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members),
            Is.EquivalentTo(new[] { "narrow", "fits1", "fits2" }),
            "wide never takes a seat in a lobby of 3");
        Assert.That(m.PoolSize, Is.EqualTo(1), "wide is left queued");

        // The same guard read the other way round. flex is wide enough that its rung 0 floor (8)
        // is out of a trio's reach, so it is skipped while it still has patience and is only
        // recruitable once its own rung has turned over - at which point it is the third seat the
        // two trios were short of.
        using var untilPatienceTurns = new Matchmaker();

        Ticket(untilPatienceTurns, player: "flex", ranges: [(AtSec: 0, Min: 8, Max: 8), (AtSec: untilPatienceTurns.Config.MaxTicketPatienceInSec, Min: 2, Max: 8)]);
        Ticket(untilPatienceTurns, player: "trio1", ranges: [(AtSec: 0, Min: 3, Max: 3)]);
        Ticket(untilPatienceTurns, player: "trio2", ranges: [(AtSec: 0, Min: 3, Max: 3)]);

        var t0 = DateTime.UtcNow;
        var tMax = t0.AddSeconds(untilPatienceTurns.Config.MaxTicketPatienceInSec);

        Assert.That(untilPatienceTurns.RunSweep(t0), Is.Empty);
        Assert.That(untilPatienceTurns.ActivePoolSize, Is.EqualTo(1), "only flex has anything left to relax");
        Assert.That(untilPatienceTurns.PoolSize, Is.EqualTo(3));

        Assert.That(untilPatienceTurns.RunSweep(t0.AddSeconds(untilPatienceTurns.Config.MaxTicketPatienceInSec / 2)), Is.Empty);

        var flexMatches = untilPatienceTurns.RunSweep(tMax);

        Assert.That(flexMatches, Has.Count.EqualTo(1));
        Assert.That(flexMatches[0].Sum(ticket => ticket.Size), Is.EqualTo(3));
        Assert.That(untilPatienceTurns.PoolSize, Is.Zero);
    }


    // Case: lowering the floor does not lower the target. MaxCount is still what the seed packs
    // for (Matchmaker.cs:541), and the settle path only runs when no full lobby was found - so a
    // rung of (4,6) seats 6 when six are there and 4 only when they are not. Both pools run at t0
    // on the same rung, so the only difference is how many players the seed had to work with.
    [Test]
    public void RangeLadderStillPacksForItsCeilingBeforeSettling() // BIG
    {
        var config = new MatchmakerConfig { MaxTicketPatienceInSec = 30 };
        using var full = new Matchmaker(config);

        foreach (string player in new[] { "p1", "p2", "p3", "p4", "p5", "p6" })
        {
            Ticket(full, player: player, ranges: [(AtSec: 0, Min: 4, Max: 6), (AtSec: 10, Min: 2, Max: 6)]);
        }

        var matches = full.RunSweep(DateTime.UtcNow);

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Sum(ticket => ticket.Size), Is.EqualTo(6), "the seed takes the ceiling of 6 it can reach");

        using var shortHanded = new Matchmaker(config);

        foreach (string player in new[] { "p1", "p2", "p3", "p4" })
        {
            Ticket(shortHanded, player: player, ranges: [(AtSec: 0, Min: 4, Max: 6), (AtSec: 10, Min: 2, Max: 6)]);
        }

        var shortMatches = shortHanded.RunSweep(DateTime.UtcNow);

        Assert.That(shortMatches, Has.Count.EqualTo(1));
        Assert.That(shortMatches[0].Sum(ticket => ticket.Size), Is.EqualTo(4), "with two fewer players the same rung falls to its floor");
    }

    // Case: MatchIndex.Upsert runs once, inside Add, so whatever it indexes is frozen at rung 0 -
    // and SearchScope narrows candidates on those two fields (:249-250, :287-288). A ticket whose
    // floor drops as it waits is exactly the ticket a smaller lobby wants to recruit, so indexing
    // rung 0 would hide it from the seeds the feature exists to introduce it to.
    //
    // a1 opens on 6-6 and narrows to 3-3, which retires it at t+10 (min == max with both ladders
    // spent, :452-455) having matched nothing - Matchmaker.cs:497 holds it back from b1 and b2
    // while they still have intervals left. At t+30 b1 settles, and the lobby it can seat depends
    // entirely on whether a1 was retrievable: three with a1, two without it.
    [Test]
    public void RelaxedFloorMakesAPassiveTicketRecruitableByASmallerLobby()
    {
        using var m = new Matchmaker(new MatchmakerConfig { MaxTicketPatienceInSec = 30 });

        // queued first, so they seed first and a1 is only ever reached as a candidate
        Ticket(m, player: "b1", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)]);
        Ticket(m, player: "b2", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)]);
        Ticket(m, player: "a1", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: 10, Min: 3, Max: 3)]);

        var t0 = DateTime.UtcNow;

        Assert.That(m.RunSweep(t0), Is.Empty, "a1 is wider than b1's ceiling");
        Assert.That(m.RunSweep(t0.AddSeconds(10)), Is.Empty, "a1 narrows to 3-3 and retires unmatched");
        Assert.That(m.ActivePoolSize, Is.EqualTo(2), "a1 is passive with both ladders spent");
        Assert.That(m.RunSweep(t0.AddSeconds(20)), Is.Empty, "b1 and b2 still have an interval left");

        var matches = m.RunSweep(t0.AddSeconds(30));

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(matches[0].Sum(ticket => ticket.Size), Is.EqualTo(3), "b1 settles into a lobby of three");
            Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "b1", "b2", "a1" }));
            Assert.That(m.PoolSize, Is.Zero, "a1 was not stranded");
        });
    }



    // ---------------------------------------------------------------------------------------
    // Sweep edge cases, first pass.
    //
    // The two tests marked FAILING BY DESIGN assert what the engine is documented to do and do not
    // pass today. They are deliberate: each one names a defect found by reading Sweep against
    // MATCHMAKER_RULES.md, QUERY-LADDER.md and UNSEATABLE-CANDIDATE.md. Both are currently commented
    // out, so neither runs at all and neither defect is guarded - restoring them is what turns them
    // back into the record they are meant to be. Do not weaken an assertion to make the suite green:
    // either fix the engine or flip the test with a note saying why the documented behaviour was
    // given up.
    // ---------------------------------------------------------------------------------------

    // FAILING BY DESIGN.
    //
    // Case: the seed packs candidates greedily and first-fit (Matchmaker.cs:362-389). A candidate
    // is committed to the first combo that will have it, and only then is that combo checked for
    // completion - so a candidate that would have filled a *later* combo exactly is spent on an
    // earlier one where it fills nothing.
    //
    //   ticket   size   accepts
    //   seed      1     everyone            min 2, max 6, queued first
    //   A         2     everyone but B
    //   B         3     everyone
    //   C         2     everyone            queued last
    //
    // A opens combo 1. B cannot join it - A will not have B - so B opens combo 2, which sits at 5
    // of the 6 seats. C is offered combo 1 first, fits, and takes it to 5. seed + B + C is a lobby
    // of exactly 6 and is never found. Today the sweep settles seed + A + C at 5 instead.
    //[Test]
    //public void SeedFindsTheCombinationThatFillsTheLobbyExactly()
    //{
    //    using var m = new Matchmaker();

    //    AddPlayerTicketRanked(m, ["p1"], skill: 1100, tolerance: 400, minCount: 2, maxCount: 6);

    //    // A's window stops short of B, so the two of them can never share a combo
    //    AddPlayerTicketRanked(m, ["p2", "p3"], skill: 1000, tolerance: 250, minCount: 2, maxCount: 6, partyId: "pA");
    //    AddPlayerTicketRanked(m, ["p4", "p5", "p6"], skill: 1400, tolerance: 500, minCount: 2, maxCount: 6, partyId: "pB");
    //    AddPlayerTicketRanked(m, ["p7", "p8"], skill: 1200, tolerance: 400, minCount: 2, maxCount: 6, partyId: "pC");

    //    var matches = m.RunSweep();

    //    Assert.That(matches, Has.Count.EqualTo(1));
    //    Assert.That(matches[0].Sum(t => t.Size), Is.EqualTo(6), "a lobby of exactly 6 was available");
    //    Assert.That(matches[0].SelectMany(t => t.Members), Is.EquivalentTo(new[] { "p1", "p4", "p5", "p6", "p7", "p8" }));
    //    Assert.That(m.PoolSize, Is.EqualTo(1), "A is the ticket the full lobby has no room for");
    //}

    // Case: the guard that stops a narrow seed spending a wider ticket that still has patience left
    // reads whether the candidate is holding out for its ceiling, and that is a reading off the
    // candidate's own age. It is therefore the same answer wherever in the pass it is taken - which
    // is the point: the guard used to run off a counter bumped per seed inside the CreatedAt-ordered
    // loop, so a candidate that had not taken its turn yet read one behind and the guard turned on
    // queue order alone.
    //
    // Same three tickets, same terms, both orders. wide wants 3 to 4 seats; n1 and n2 want 2 to 3.
    // Either way all three seat at 3 once the pool is spent: whichever order they queued in, n1
    // reads wide as out of patience, so wide is not orphaned above a minimum it can never reach.
    [TestCase(true)]
    [TestCase(false)]
    public void WideTicketIsRecruitableWhicheverOrderThePoolQueuedIn(bool wideFirst)
    {
        var config = new MatchmakerConfig();

        var (matches, poolSize) = SweepInQueueOrder(
            config, wideFirst, sweepAtSec: config.MaxTicketPatienceInSec,
            first: m => Ticket(m, player: "wide", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 3, Max: 4)]),
            second: m =>
            {
                Ticket(m, player: "n1", ranges: [(AtSec: 0, Min: 3, Max: 3), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 3)]);
                Ticket(m, player: "n2", ranges: [(AtSec: 0, Min: 3, Max: 3), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 3)]);
            });

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "wide", "n1", "n2" }));
        Assert.That(poolSize, Is.Zero);
    }

    // FAILING BY DESIGN.
    //
    // Case: the same-properties rule compares the *field names* a rung constrains
    // (MatchQueryParser.cs:143-151, via Fields/Collect at :161-191) and nothing else. The rule
    // exists to stop a rung turning into a different ticket - QUERY-LADDER.md:94-97 names dropping
    // "+properties.mode:ranked" as the case it guards - but a rung that keeps the field and swaps
    // the value, or keeps the field and flips MUST to MUST_NOT, produces exactly that outcome and
    // is accepted today.
    //[Test]
    //public void LadderRungMayNotChangeWhatAPropertyMustEqual()
    //{
    //    using var valueSwap = new Matchmaker();

    //    var swapped = Assert.Throws<MatchmakerException>(() => valueSwap.Add(
    //        sessionIds: ["p1"],
    //        ownerSessionId: "p1",
    //        partyId: "",
    //        queryLadder: [(0, "+properties.mode:ranked"), (30, "+properties.mode:casual")],
    //        properties: new Dictionary<string, object> { ["mode"] = "ranked" },
    //        minMaxLadder: [(0, 4, 4), (30, 2, 4)]), "rung 2 seats across modes, which is what the rule exists to prevent");

    //    //Assert.That(swapped!.Message, Is.EqualTo(MatchmakerException.QueryPropertiesDiffer));
    //    //Assert.That(valueSwap.PoolSize, Is.Zero);

    //    using var inverted = new Matchmaker();

    //    var flipped = Assert.Throws<MatchmakerException>(() => inverted.Add(
    //        sessionIds: ["p1"],
    //        ownerSessionId: "p1",
    //        partyId: "",
    //        queryLadder:
    //        [
    //            (0, "+properties.mode:ranked +properties.region:eu"),
    //            (30, "+properties.region:eu -properties.mode:ranked"),
    //        ],
    //        properties: new Dictionary<string, object> { ["mode"] = "ranked", ["region"] = "eu" },
    //        minMaxLadder: [(0, 4, 4), (30, 2, 4)]), "rung 2 constrains the same two fields and means the opposite");

    //    //Assert.That(flipped!.Message, Is.EqualTo(MatchmakerException.QueryPropertiesDiffer));
    //    //Assert.That(inverted.PoolSize, Is.Zero);

    //    // control: the legitimate shape the rule is meant to allow - same fields, moved bounds
    //    using var relaxing = new Matchmaker();

    //    Assert.DoesNotThrow(() => relaxing.Add(
    //        sessionIds: ["p1"],
    //        ownerSessionId: "p1",
    //        partyId: "",
    //        queryLadder:
    //        [
    //            (0, "+properties.mode:ranked +properties.skill:[500 TO 600]"),
    //            (30, "+properties.mode:ranked +properties.skill:[300 TO 800]"),
    //        ],
    //        properties: new Dictionary<string, object> { ["mode"] = "ranked", ["skill"] = 550 },
    //        minMaxLadder: [(0, 4, 4), (30, 2, 4)]));

    //    Assert.That(relaxing.PoolSize, Is.EqualTo(1));
    //}

    [Test]
    public void PassiveLadderTicketKeepsClimbing()
    {
        using var m = new Matchmaker(new MatchmakerConfig { MaxTicketPatienceInSec = 20 });

        Ticket(m, player: "waiter", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)],
            queries: [
                (AtSec: 0, Query: "+properties.mode:ranked +properties.skill:[900 TO 1100]"),
                (AtSec: 10, Query: "+properties.mode:ranked +properties.skill:[500 TO 1500]"),
            ],
            properties: new() { ["mode"] = "ranked", ["skill"] = 1000 });

        var t0 = DateTime.UtcNow;

        var tMax = t0.AddSeconds(m.Config.MaxTicketPatienceInSec);

        Assert.That(m.RunSweep(t0), Is.Empty, "waiter has nobody to match with");
        Assert.That(m.RunSweep(tMax), Is.Empty, "waiter still has nobody to match with");
        Assert.That(m.ActivePoolSize, Is.Zero, "waiter has spent its patience and gone passive");
        Assert.That(m.PoolSize, Is.EqualTo(1));

        Ticket(m, player: "partner", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)],
            "+properties.mode:ranked +properties.skill:[900 TO 2100]",
            properties: new() { ["mode"] = "ranked", ["skill"] = 1500 });

        // partner queued just now, so the instant that spends waiter's patience leaves partner
        // still holding out for the full lobby - it settles a whole patience of its own later
        var tPartnerMax = DateTime.UtcNow.AddSeconds(m.Config.MaxTicketPatienceInSec);

        var matches = m.RunSweep(tPartnerMax);

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "waiter", "partner" }));

        using var singleRung = new Matchmaker(new MatchmakerConfig { MaxTicketPatienceInSec = 20 });

        Ticket(singleRung, player: "waiter", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: singleRung.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)],
            "+properties.skill:[900 TO 1100]", properties: new() { ["skill"] = 1000 });

        var singleRungT0 = DateTime.UtcNow;

        var singleRungTMax = singleRungT0.AddSeconds(singleRung.Config.MaxTicketPatienceInSec);

        Assert.That(singleRung.RunSweep(singleRungT0), Is.Empty);
        Assert.That(singleRung.RunSweep(singleRungTMax), Is.Empty);

        Ticket(singleRung, player: "partner", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: singleRung.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)],
            "+properties.skill:[900 TO 2100]", properties: new() { ["skill"] = 1500 });

        var singleRungPartnerMax = DateTime.UtcNow.AddSeconds(singleRung.Config.MaxTicketPatienceInSec);

        Assert.That(singleRung.RunSweep(singleRungPartnerMax), Is.Empty, "a single-rung ticket has nothing to climb to");
        Assert.That(singleRung.PoolSize, Is.EqualTo(2));
    }

    // Validations

    // Case: Add validates a ticket whole before it queues any of it - both ladders rung by rung,
    // the shape they make across rungs, the cap on how many rungs there may be, and the roster,
    // query and properties they arrive with. Nothing a rejected Add touched stays behind, and the
    // legal ticket sitting next to each rule is where the line it draws actually falls.
    [Test]
    public void AddValidatesTheWholeTicketBeforeQueueingIt()
    {
        using var m = new Matchmaker(new MatchmakerConfig { MaxTicketPatienceInSec = 30 });

        // every rung is validated, not just the first: each ladder named for rung 1 below has a
        // rung 0 the validator accepts and fails on the rung it only reaches by walking the rest
        // of the ladder. The others fail on rung 0, or before it in the case of countMultiple.
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(
                () => Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 1, Max: 4)]),
                "rung 1's minCount must be at least 2");
            Assert.Throws<ArgumentException>(
                () => Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 3, Max: 3), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 4, Max: 3)]),
                "rung 1's maxCount must be at least its minCount");
            Assert.Throws<ArgumentException>(
                () => Party(m, "party", ["m1", "m2", "m3", "m4", "m5", "m6"], ranges: [(0, 6, 8), (10, 2, 4)]),
                "rung 1's maxCount must fit the roster");

            Assert.Throws<ArgumentException>(
                () => Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 4, Max: 4), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 2, Max: 4)], countMultiple: 0),
                "countMultiple must be at least 1");
            Assert.Throws<ArgumentException>(
                () => Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 4, Max: 6)], countMultiple: 7),
                "the range must contain a multiple");
            Assert.Throws<ArgumentException>(
                () => Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 4, Max: 6)], countMultiple: 4),
                "maxCount must be a multiple of countMultiple");
            Assert.Throws<ArgumentException>(
                () => Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 4, Max: 8), (AtSec: 10, Min: 4, Max: 6)], countMultiple: 4),
                "rung 1's maxCount must be a multiple of countMultiple");
            Assert.Throws<ArgumentException>(
                () => Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 8, Max: 8), (AtSec: m.Config.MaxTicketPatienceInSec, Min: 5, Max: 8)], countMultiple: 2),
                "rung 1's minCount must be a multiple of countMultiple");
            Assert.Throws<ArgumentException>(
                () => Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 4, Max: 8), (AtSec: 10, Min: 4, Max: 6), (AtSec: 10, Min: 4, Max: 6)], countMultiple: 2),
                "rung cannot duplicate");
            Assert.Throws<ArgumentException>(
                () => Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 2, Max: 6), (AtSec: 10, Min: 4, Max: 6)]),
                "a rung's floor may not rise");
            Assert.Throws<ArgumentException>(
                () => Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 4, Max: 6), (AtSec: 10, Min: 2, Max: 8)]),
                "a rung's ceiling may not rise");
        });

        // the same for what the ticket is queued with rather than what it asks for
        Assert.Throws<ArgumentException>(() => m.Add(
            sessionIds: [],
            ownerSessionId: "p1",
            partyId: "",
            query: "+properties.mode:ranked",
            properties: new() { ["mode"] = "ranked" },
            minMaxLadder: [(0, 2, 2)]), "the roster must not be empty");

        Assert.Throws<ArgumentException>(() => m.Add(
            sessionIds: ["p2"],
            ownerSessionId: "p1",
            partyId: "",
            query: "+properties.mode:ranked",
            properties: new() { ["mode"] = "ranked" },
            minMaxLadder: [(0, 2, 2)]), "the owner must be in the roster");

        Assert.Throws<ArgumentException>(() => m.Add(
            sessionIds: ["p1"],
            ownerSessionId: "p1",
            partyId: "",
            query: "+properties.mode:[unclosed",
            properties: new() { ["mode"] = "ranked" },
            minMaxLadder: [(0, 2, 2)]), "the query must parse");

        Assert.Throws<ArgumentException>(() => m.Add(
            sessionIds: ["p1"],
            ownerSessionId: "p1",
            partyId: "",
            query: "+properties.mode:ranked",
            properties: new() { ["mode"] = "ranked", ["joined_at"] = DateTime.UtcNow },
            minMaxLadder: [(0, 2, 2)]), "every property must be a type the index can store");

        // a query ladder is one query relaxing, so a rung that drops a property altogether is not
        // a wider rung of the same query - it is a different question, and rejected as one
        var error = Assert.Throws<MatchmakerException>(() => m.Add(
            sessionIds: ["p1"],
            ownerSessionId: "p1",
            partyId: "",
            queryLadder: [(0, "+properties.mode:ranked +properties.skill:[500 TO 600]"), (10, "+properties.mode:ranked")],
            properties: new() { ["mode"] = "ranked", ["skill"] = 550 },
            minMaxLadder: [(0, 4, 4), (m.Config.MaxTicketPatienceInSec, 2, 4)]));

        Assert.That(error!.Message, Is.EqualTo(MatchmakerException.QueryPropertiesDiffer));

        Assert.That(m.PoolSize, Is.Zero, "no rejected ticket left partial state");
        Assert.That(m.ActivePoolSize, Is.Zero);

        Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 2, Max: 2)]);
        Ticket(m, player: "p2", ranges: [(AtSec: 0, Min: 2, Max: 2)]);

        Assert.That(m.RunSweep(), Has.Count.EqualTo(1), "the rejections left a matchmaker that still seats a legal pair");
        Assert.That(m.PoolSize, Is.Zero);

        // and the legal side of the rules the rejections above draw
        Assert.DoesNotThrow(() => m.Add(
            sessionIds: ["p3"],
            ownerSessionId: "p3",
            partyId: "",
            queryLadder: [(0, "+properties.mode:ranked +properties.skill:[500 TO 600]"), (10, "+properties.mode:ranked +properties.skill:[300 TO 800]")],
            properties: new() { ["mode"] = "ranked", ["skill"] = 550 },
            minMaxLadder: [(0, 4, 4), (m.Config.MaxTicketPatienceInSec, 2, 4)]));

        Assert.DoesNotThrow(() => Ticket(m, player: "p4", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: 10, Min: 4, Max: 6)]),
            "a falling floor with a held ceiling is allowed");
        Assert.DoesNotThrow(() => Ticket(m, player: "p5", ranges: [(AtSec: 0, Min: 6, Max: 8), (AtSec: 10, Min: 4, Max: 6)]),
            "a falling floor and ceiling are allowed");

        Assert.That(m.PoolSize, Is.EqualTo(3), "only the legal ladders queued");

        // where a rung sits and how many there are are different questions. MaxLadderRungs answers
        // the second one, and answers it the same way wherever on the clock the caller puts them -
        // a cap of 3 is three rungs whether they are named a second apart or a patience apart.
        using var capped = new Matchmaker(new MatchmakerConfig
        {
            MaxTicketPatienceInSec = 30,
            MaxLadderRungs = 3,
        });

        Ticket(capped, player: "p1", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: capped.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)],
            queries: [
                (AtSec: 0, Query: "+properties.skill:[900 TO 1100]"),
                (AtSec: 15, Query: "+properties.skill:[400 TO 1600]"),
                (AtSec: 30, Query: "*"),
            ],
            properties: new() { ["skill"] = 1000 });

        Assert.Throws<ArgumentException>(
            () => Ticket(capped, player: "p2", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: capped.Config.MaxTicketPatienceInSec, Min: 2, Max: 6)],
                queries: [
                    (AtSec: 0, Query: "+properties.skill:[900 TO 1100]"),
                    (AtSec: 5, Query: "+properties.skill:[800 TO 1200]"),
                    (AtSec: 15, Query: "+properties.skill:[400 TO 1600]"),
                    (AtSec: 30, Query: "*"),
                ],
                properties: new() { ["skill"] = 1000 }),
            "a four-rung query ladder is over the cap of three");

        Ticket(capped, player: "p3", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: 5, Min: 4, Max: 6), (AtSec: 30, Min: 2, Max: 6)]);

        Assert.Throws<ArgumentException>(
            () => Ticket(capped, player: "p4", ranges: [(AtSec: 0, Min: 6, Max: 6), (AtSec: 5, Min: 4, Max: 6), (AtSec: 20, Min: 4, Max: 4), (AtSec: 30, Min: 2, Max: 4)]),
            "a four-rung range ladder is over the same cap");

        Assert.That(capped.PoolSize, Is.EqualTo(2), "only the two legal ladders are queued");

        // the cap takes any positive count, including 1 - a deployment that wants no ladders at
        // all rather than a bound on their length
        using var noLadders = new Matchmaker(new MatchmakerConfig { MaxLadderRungs = 1 });

        Assert.Throws<ArgumentException>(
            () => Ticket(noLadders, player: "p1", ranges: [(AtSec: 0, Min: 4, Max: 4)], queries: [(AtSec: 0, Query: "*"), (AtSec: 30, Query: "*")]),
            "a two-rung query ladder is over the cap of one");

        Ticket(noLadders, player: "p2", ranges: [(AtSec: 0, Min: 2, Max: 4)]);
        Assert.That(noLadders.PoolSize, Is.EqualTo(1), "the single-query form is one rung and fits");
    }

    [Test]
    public void InvalidConfigValuesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new Matchmaker(new MatchmakerConfig { MaxTicketPatienceInSec = 0 }), "MaxTicketPatienceInSec must be positive");
        Assert.Throws<ArgumentException>(() => new Matchmaker(new MatchmakerConfig { MaxTicketPatienceInSec = -1 }));
        Assert.Throws<ArgumentException>(() => new Matchmaker(new MatchmakerConfig { MaxLadderRungs = -1 }), "MaxLadderRungs must not be negative");
    }

    [Test]
    public void DegenerateCallsAreNoOps() // OK
    {
        using var m = new Matchmaker();

        Assert.That(m.RunSweep(), Is.Empty, "an empty pool matches nobody");
        Assert.That(m.CancelTicket("no-such-ticket"), Is.False, "an unknown ticket id cancels nothing");

        string ticket = Ticket(m, player: "p1", ranges: [(AtSec: 0, Min: 2, Max: 2)]).Ticket;

        Assert.That(m.CancelTicket(ticket), Is.True);
        Assert.That(m.CancelTicket(ticket), Is.False, "the second cancel of a ticket does nothing");
        Assert.That(m.PoolSize, Is.Zero);
    }

    // Adds `first` and `second` to a fresh matchmaker in the order `firstLeads` selects, then takes
    // one pass `sweepAtSec` seconds after the adds - so queue order is the only thing that varies
    // between the two rows, and anything they disagree on is a reading that depends on it.
    static (List<List<MatchmakerTicket>> Matches, int PoolSize) SweepInQueueOrder(
        MatchmakerConfig config, bool firstLeads, int sweepAtSec,
        Action<Matchmaker> first, Action<Matchmaker> second)
    {
        using var m = new Matchmaker(config);

        if (firstLeads)
        {
            first(m);
            second(m);
        }
        else
        {
            second(m);
            first(m);
        }

        var matches = m.RunSweep(DateTime.UtcNow.AddSeconds(sweepAtSec));

        return (matches, m.PoolSize);
    }
}
