namespace Sukhoi.Tests;

// These test cases cover real-world matchmaking scenarios, and also serve as examples
public class GameModeTests
{
    // Example Case (5v5 Ranked): every player should match within it's MMR and MMR tolerance
    [Test]
    public void RankedFiveVsFiveMatchesSimple()
    {
        using var m = new Matchmaker();
        const int TeamSize = 5;

        var oneShort = PopulatePoolRanked(m, "oneShort", count: 9, mmr: 3000, tolerance: 50, teamSize: TeamSize);
        var bronze = PopulatePoolRanked(m, "bronze", count: 10, mmr: 1000, tolerance: 50, teamSize: TeamSize);
        var silver = PopulatePoolRanked(m, "silver", count: 10, mmr: 2000, tolerance: 50, teamSize: TeamSize);

        var t0 = DateTime.UtcNow;

        var matches = m.RunSweep(t0);

        Assert.That(matches, Has.Count.EqualTo(2));
        foreach (var match in matches)
        {
            Assert.That(match.Sum(ticket => ticket.Size), Is.EqualTo(TeamSize * 2));
            Assert.That(match.Select(ticket => Convert.ToInt32(ticket.Properties["skill"])).Distinct(), Has.Exactly(1).Items);
        }

        var matched = matches.SelectMany(match => match).SelectMany(ticket => ticket.Members);
        Assert.That(matched, Is.EquivalentTo(bronze.Concat(silver)));
        Assert.That(m.PoolSize, Is.EqualTo(oneShort.Count));
    }

    // Example Case: a ranked 5v5 queue with mixed MMR and mixed patience for skill gaps (50/100/300).
    // A gap is only crossed when both sides agree on it - one player being generous is not enough.
    [Test]
    public void RankedFiveVsFiveWithTolerance()
    {
        using var m = new Matchmaker();
        const int TeamSize = 5;

        List<string> Queue(string prefix, int mmr, int tolerance, int gap = 0) =>
            PopulatePoolRanked(m, prefix, count: TeamSize, mmr: mmr, tolerance: tolerance, teamSize: TeamSize,
                gap: gap);

        (string Tier, int Mmr, int Tolerance)[] tiers =
            [("bronze", 1000, 50), ("silver", 2000, 100), ("gold", 3000, 300)];

        var matchable = new List<string>();
        foreach (var (tier, mmr, tolerance) in tiers)
        {
            matchable.AddRange(Queue($"{tier}Low", mmr, tolerance));
            matchable.AddRange(Queue($"{tier}High", mmr + tolerance, tolerance));
        }

        List<string> unmatchable =
        [
            .. Queue("generous", mmr: 1200, tolerance: 300),
            .. Queue("tooFarLow", mmr: 4000, tolerance: 50),
            .. Queue("tooFarHigh", mmr: 4051, tolerance: 50),
            .. Queue("loner", mmr: 9000, tolerance: 50, gap: 1000),
        ];

        Assert.That(m.PoolSize, Is.EqualTo(matchable.Count + unmatchable.Count));

        var t0 = DateTime.UtcNow;

        var matches = m.RunSweep(t0);

        Assert.That(matches, Has.Count.EqualTo(3));

        foreach (var match in matches)
        {
            Assert.That(match.Sum(ticket => ticket.Size), Is.EqualTo(TeamSize * 2));

            foreach (var ticket in match)
            {
                int skill = Convert.ToInt32(ticket.Properties["skill"]);
                int furthest = match.Max(other => Math.Abs(skill - Convert.ToInt32(other.Properties["skill"])));

                Assert.That(furthest, Is.LessThanOrEqualTo(Convert.ToInt32(ticket.Properties["tolerance"])),
                    $"{ticket.OwningSessionId} was put with someone outside its MMR range");
            }
        }

        var matched = matches.SelectMany(match => match).SelectMany(ticket => ticket.Members);
        Assert.That(matched, Is.EquivalentTo(matchable));
        Assert.That(m.PoolSize, Is.EqualTo(unmatchable.Count));
    }

    // Example Case (co-op role queue with (1/1/1) composition): a game needs one tank, one dps and one support
    [Test]
    public void CoopRoleQueueOnlyFormsOneTankOneDpsOneSupport()
    {
        using var m = new Matchmaker();
        const int MatchSize = 3;

        string[] roles = ["tank", "dps", "support"];

        void AddPlayer(string player, string role) =>
            Ticket(m, player, [(0, MatchSize, MatchSize)],
                query: $"+properties.mode:coop -properties.role:{role}",
                properties: new() { ["mode"] = "coop", ["role"] = role });

        foreach (string role in roles)
        {
            for (int slot = 0; slot < 2; slot++)
            {
                AddPlayer($"{role}{slot}", role: role);
            }
        }

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(2));

        foreach (var match in matches)
        {
            Assert.That(match.Select(ticket => (string)ticket.Properties["role"]), Is.EquivalentTo(roles));
        }

        Assert.That(m.PoolSize, Is.Zero);
    }

    [Test]
    public void CoopRoleQueueFormsOneTankTwoDpsTwoSupport()
    {
        using var m = new Matchmaker();
        const string SeatQueueMode = "roleq";
        Dictionary<string, string[]> SeatsByRole = new()
        {
            ["tank"] = ["tank"],
            ["dps"] = ["dps1", "dps2"],
            ["support"] = ["support1", "support2"],
        };
        string[] TeamSeats = [.. SeatsByRole.Values.SelectMany(seats => seats)];
        int SeatQueueTeamSize = TeamSeats.Length;

        void AddPlayer(string player, string role)
        {
            foreach (string seat in SeatsByRole[role])
            {
                Ticket(m, player, [(0, SeatQueueTeamSize, SeatQueueTeamSize)],
                    query: $"+properties.mode:{SeatQueueMode} -properties.seat:{seat}",
                    properties: new() { ["mode"] = SeatQueueMode, ["role"] = role, ["seat"] = seat });
            }
        }

        AddPlayer("p1", role: "tank");
        AddPlayer("p2", role: "dps");
        AddPlayer("p3", role: "support");
        AddPlayer("p4", role: "support");

        var t0 = DateTime.UtcNow;

        var matches = m.RunSweep(t0);

        Assert.That(matches, Is.Empty);
        Assert.That(m.PoolSize, Is.EqualTo(7), "one tank ticket, two dps tickets and four support tickets");
        Assert.That(m.ActivePoolSize, Is.Zero);

        AddPlayer("p5", role: "dps");

        var secondSweepMatches = m.RunSweep();

        Assert.That(secondSweepMatches, Has.Count.EqualTo(1));

        var match = secondSweepMatches[0];
        Assert.Multiple(() =>
        {
            Assert.That(match.SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4", "p5" }));
            Assert.That(match.Select(ticket => (string)ticket.Properties["role"]), Is.EquivalentTo(new[] { "tank", "dps", "dps", "support", "support" }));
            Assert.That(match.Select(ticket => (string)ticket.Properties["seat"]), Is.EquivalentTo(TeamSeats));
            Assert.That(match.Sum(ticket => ticket.Size), Is.EqualTo(SeatQueueTeamSize));
        });

        Assert.That(m.PoolSize, Is.Zero);
    }

    // Example Case: the same (1/2/2) composition, with every seat queued before the first sweep -
    // the lobby has to form in one pass rather than being completed by a later arrival
    [Test]
    public void CoopRoleQueueFormsOneTankTwoDpsTwoSupportInASingleSweep()
    {
        using var m = new Matchmaker();
        const string SeatQueueMode = "roleq";
        Dictionary<string, string[]> SeatsByRole = new()
        {
            ["tank"] = ["tank"],
            ["dps"] = ["dps1", "dps2"],
            ["support"] = ["support1", "support2"],
        };
        string[] TeamSeats = [.. SeatsByRole.Values.SelectMany(seats => seats)];
        int SeatQueueTeamSize = TeamSeats.Length;

        void AddPlayer(string player, string role)
        {
            foreach (string seat in SeatsByRole[role])
            {
                Ticket(m, player, [(0, SeatQueueTeamSize, SeatQueueTeamSize)],
                    query: $"+properties.mode:{SeatQueueMode} -properties.seat:{seat}",
                    properties: new() { ["mode"] = SeatQueueMode, ["role"] = role, ["seat"] = seat });
            }
        }

        AddPlayer("tank1", role: "tank");
        AddPlayer("dps1", role: "dps");
        AddPlayer("dps2", role: "dps");
        AddPlayer("support1", role: "support");
        AddPlayer("support2", role: "support");

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));

        var match = matches[0];
        Assert.Multiple(() =>
        {
            Assert.That(match.SelectMany(ticket => ticket.Members),
                Is.EquivalentTo(new[] { "tank1", "dps1", "dps2", "support1", "support2" }));
            Assert.That(match.Select(ticket => (string)ticket.Properties["role"]),
                Is.EquivalentTo(new[] { "tank", "dps", "dps", "support", "support" }));
            Assert.That(match.Select(ticket => (string)ticket.Properties["seat"]), Is.EquivalentTo(TeamSeats));
            Assert.That(match.Sum(ticket => ticket.Size), Is.EqualTo(SeatQueueTeamSize));
        });

        Assert.That(m.PoolSize, Is.Zero);
    }

    // Example Case (picky and flexible players): the game lets a player either name the maps they want or say "any map is fine"
    [Test]
    public void MapPreferenceMatchesPickyPlayersWithFlexiblePlayers()
    {
        using var m = new Matchmaker();
        const string MapPrefix = "map_";
        const int MatchSize = 4;
        string[] allMaps = ["dust2", "inferno", "nuke"];

        // Maps a ticket says yes to, read back off its properties
        HashSet<string> AcceptedMaps(MatchmakerTicket ticket) =>
        [
            .. ticket.Properties.Keys
                .Where(key => key.StartsWith(MapPrefix))
                .Select(key => key[MapPrefix.Length..])
        ];

        // Maps every member of a match agreed on
        IEnumerable<string> CommonMaps(List<MatchmakerTicket> match) =>
            match.Select(AcceptedMaps)
                .Aggregate((shared, maps) => [.. shared.Intersect(maps)]);

        void AddPlayer(string player, params string[] maps) =>
            Ticket(m, player, [(0, MatchSize, MatchSize)],
                query:
                $"+properties.mode:comp +({string.Join(" OR ", maps.Select(map => $"properties.{MapPrefix}{map}:T"))})",
                properties: maps.Select(map => KeyValuePair.Create($"{MapPrefix}{map}", (object)true))
                    .Append(KeyValuePair.Create("mode", (object)"comp"))
                    .ToDictionary());

        void AssertMatch(List<MatchmakerTicket> match, string[] expectedPlayers, string expectedMap) =>
            Assert.Multiple(() =>
            {
                Assert.That(match.SelectMany(ticket => ticket.Members), Is.EquivalentTo(expectedPlayers));
                Assert.That(CommonMaps(match), Is.EquivalentTo(new[] { expectedMap }));
            });

        AddPlayer("p1_dustOrInferno", "dust2", "inferno");
        AddPlayer("p2_dust", "dust2");
        AddPlayer("p3_any", allMaps);
        AddPlayer("p4_any", allMaps);
        AddPlayer("p5_nuke", "nuke");
        AddPlayer("p6_nuke", "nuke");

        var firstSweepMatches = m.RunSweep();

        Assert.That(firstSweepMatches, Has.Count.EqualTo(1));
        AssertMatch(firstSweepMatches[0], expectedPlayers: ["p1_dustOrInferno", "p2_dust", "p3_any", "p4_any"],
            expectedMap: "dust2");

        AddPlayer("p7_dustOrInferno", "dust2", "inferno");
        AddPlayer("p8_inferno", "inferno");
        AddPlayer("p9_any", allMaps);
        AddPlayer("p10_any", allMaps);

        var secondSweepMatches = m.RunSweep();

        Assert.That(secondSweepMatches, Has.Count.EqualTo(1));
        AssertMatch(secondSweepMatches[0], expectedPlayers: ["p7_dustOrInferno", "p8_inferno", "p9_any", "p10_any"],
            expectedMap: "inferno");

        Assert.That(m.PoolSize, Is.EqualTo(2), "the two nuke players never found a fourth");
    }

    // Example Case: the same picky-and-flexible setup, written with regex instead - player also can say ("any defusal map", "anything at all")
    [Test]
    public void RegexMapPreferenceMatchesPickyPlayersWithFlexiblePlayers()
    {
        using var m = new Matchmaker();
        const int MatchSize = 4;

        IEnumerable<string> ChosenMaps(List<MatchmakerTicket> match) =>
            match.Select(ticket => (string)ticket.Properties["map"]);

        void AddPlayer(string playerId, string map, string willingToPlay) =>
            Ticket(m, playerId, [(0, MatchSize, MatchSize)],
                query: $"+properties.mode:comp +properties.map:/{willingToPlay}/",
                properties: new() { ["mode"] = "comp", ["map"] = map });

        void AssertMatch(List<MatchmakerTicket> match, string[] expectedPlayers, string[] expectedMaps) =>
            Assert.Multiple(() =>
            {
                Assert.That(match.SelectMany(ticket => ticket.Members), Is.EquivalentTo(expectedPlayers));
                Assert.That(ChosenMaps(match), Is.EquivalentTo(expectedMaps));
            });

        AddPlayer("p1_dust", map: "de_dust2", willingToPlay: "de_dust2|any");
        AddPlayer("p2_dust", map: "de_dust2", willingToPlay: "de_dust2|any");
        AddPlayer("p3_any", map: "any", willingToPlay: ".*");
        AddPlayer("p4_any", map: "any", willingToPlay: ".*");
        AddPlayer("p5_office", map: "cs_office", willingToPlay: ".*");

        var firstSweepMatches = m.RunSweep();

        Assert.That(firstSweepMatches, Has.Count.EqualTo(1));
        AssertMatch(firstSweepMatches[0], expectedPlayers: ["p1_dust", "p2_dust", "p3_any", "p4_any"],
            expectedMaps: ["de_dust2", "de_dust2", "any", "any"]);

        Assert.That(m.ActivePoolSize, Is.Zero);
        Assert.That(m.PoolSize, Is.EqualTo(1), "p5_office is alone on cs_office");

        AddPlayer("p6_defusalAny", map: "de_inferno", willingToPlay: "de_.*|any");
        AddPlayer("p7_defusalAny", map: "de_nuke", willingToPlay: "de_.*|any");
        AddPlayer("p8_any", map: "any", willingToPlay: ".*");
        AddPlayer("p9_any", map: "any", willingToPlay: ".*");

        var secondSweepMatches = m.RunSweep();

        Assert.That(secondSweepMatches, Has.Count.EqualTo(1));
        AssertMatch(secondSweepMatches[0], expectedPlayers: ["p6_defusalAny", "p7_defusalAny", "p8_any", "p9_any"],
            expectedMaps: ["de_inferno", "de_nuke", "any", "any"]);

        Assert.That(m.PoolSize, Is.EqualTo(1), "p5_office never found a lobby");
    }

    // Example Case: one queue serving 10 game modes - every player picks a single mode at random
    [Test]
    public void TenGameModesEachFillTheirOwnFiveVsFive()
    {
        using var m = new Matchmaker();
        const int MatchSize = 10; // 5v5
        const int PlayerCount = 200;
        string[] gameModes =
        [
            "domination", "deathmatch", "ctf", "koth", "payload",
            "escort", "hardpoint", "control", "breakthrough", "gunGame",
        ];

        var rng = new Random(1234);
        var queuedPerMode = gameModes.ToDictionary(mode => mode, _ => new List<string>());

        IEnumerable<string> Members(IEnumerable<List<MatchmakerTicket>> matches) =>
            matches.SelectMany(match => match).SelectMany(ticket => ticket.Members);

        void AddPlayer(string player, string mode) =>
            Ticket(m, player, [(0, MatchSize, MatchSize)],
                $"+properties.mode:{mode}", new() { ["mode"] = mode });

        for (int i = 0; i < PlayerCount; i++)
        {
            string mode = gameModes[rng.Next(gameModes.Length)];
            string playerId = $"p{i}";
            queuedPerMode[mode].Add(playerId);
            AddPlayer(playerId, mode);
        }

        var t0 = DateTime.UtcNow;

        var matches = m.RunSweep(t0);

        int expectedMatches = queuedPerMode.Values.Sum(queued => queued.Count / MatchSize);

        Assert.Multiple(() =>
        {
            Assert.That(matches, Has.Count.EqualTo(expectedMatches));
            Assert.That(m.PoolSize, Is.EqualTo(PlayerCount - expectedMatches * MatchSize));
            Assert.That(Members(matches), Is.Unique, "a player was seated in two games at once");
            Assert.That(matches.Select(match => match.Sum(ticket => ticket.Size)), Is.All.EqualTo(MatchSize));
            Assert.That(matches.Select(match => match.Select(ticket => (string)ticket.Properties["mode"]).Distinct().Count()), Is.All.EqualTo(1),
                "a match mixed two game modes");
        });

        var matchesPerMode = matches.ToLookup(match => (string)match[0].Properties["mode"]);
        foreach (var (mode, queued) in queuedPerMode)
        {
            var seated = Members(matchesPerMode[mode]).ToList();
            Assert.That(seated, Is.SubsetOf(queued), $"someone was seated in {mode} without queueing for it");
            Assert.That(seated, Has.Count.EqualTo(queued.Count / MatchSize * MatchSize));
        }
    }

    // Example Case: a player queues for three modes at once and must be seated in exactly one of them
    [Test]
    public void PlayerQueuedForThreeModesIsMatchedIntoOnlyOneOfThem()
    {
        using var m = new Matchmaker();
        const int MatchSize = 2;

        IEnumerable<string> Members(IEnumerable<List<MatchmakerTicket>> matches) =>
            matches.SelectMany(match => match).SelectMany(ticket => ticket.Members);

        void AddPlayer(string player, string mode) =>
            Ticket(m, player, [(0, MatchSize, MatchSize)],
                $"+properties.mode:{mode}", new() { ["mode"] = mode });

        // Sweep 1: p1 is queued for all three modes, but only casual has a partner ready
        AddPlayer("p1", "casual");
        AddPlayer("p1", "ranked");
        AddPlayer("p1", "arcade");
        AddPlayer("casualPartner", "casual");
        AddPlayer("rankedPartner", "ranked");
        AddPlayer("arcadePartner", "arcade");

        var firstSweepMatches = m.RunSweep();

        Assert.That(firstSweepMatches, Has.Count.EqualTo(1));

        Assert.Multiple(() =>
        {
            Assert.That(Members(firstSweepMatches), Is.EquivalentTo(new[] { "p1", "casualPartner" }),
                "p1 was seated in more than one match at once");
            Assert.That(m.PoolSize, Is.EqualTo(2),
                "p1's ranked and arcade tickets are cancelled, leaving the two partners");
            Assert.That(m.ActivePoolSize, Is.Zero);
        });

        // Sweep 2: fresh partners let the leftover ranked and arcade players form their own matches
        AddPlayer("rankedPartner2", "ranked");
        AddPlayer("arcadePartner2", "arcade");

        var secondSweepMatches = m.RunSweep();

        Assert.That(secondSweepMatches, Has.Count.EqualTo(2));

        Assert.Multiple(() =>
        {
            Assert.That(Members(secondSweepMatches), Is.EquivalentTo(
                new[] { "rankedPartner", "rankedPartner2", "arcadePartner", "arcadePartner2" }));
            Assert.That(m.PoolSize, Is.Zero);
        });
    }

    static List<string> PopulatePoolRanked(Matchmaker matchmaker, string prefix, int count, int mmr,
        int tolerance, int teamSize, int gap = 0)
    {
        var players = new List<string>(count);

        for (int i = 0; i < count; i++)
        {
            int skill = mmr + i * gap;
            string player = $"{prefix}{i + 1}";
            players.Add(player);

            matchmaker.Add(
                sessionIds: [player],
                ownerSessionId: player,
                partyId: "",
                query: $"+properties.mode:ranked +properties.skill:[{skill - tolerance} TO {skill + tolerance}]",
                properties: new()
                {
                    ["mode"] = "ranked",
                    ["skill"] = skill,
                    ["tolerance"] = tolerance,
                },
                minMaxLadder: [(0, teamSize * 2, teamSize * 2)],
                countMultiple: 1);
        }

        return players;
    }
}
