namespace Sukhoi.Tests;

// These test cases cover real-world matchmaking scenarios, and also serve as examples
public class GameModeTests
{
    // Example Case (simple 5v5): no preferences at all - any ten players in the pool make a lobby
    [Test]
    public void SimpleFiveVsFiveMatchesAnyTenPlayers()
    {
        using var m = new Matchmaker();
        const int TeamSize = 5;
        const int PlayerCount = 29;

        var queued = new List<string>();
        for (int i = 1; i <= PlayerCount; i++)
        {
            string player = $"p{i}";
            queued.Add(player);

            Ticket(m, player: player, [(AtSec: 0, Min: TeamSize * 2, Max: TeamSize * 2)], query: "*");
        }

        var t0 = DateTime.UtcNow;

        var matches = m.RunSweep(t0);

        Assert.That(matches, Has.Count.EqualTo(2));
        foreach (var match in matches)
        {
            Assert.That(match.Sum(ticket => ticket.Size), Is.EqualTo(TeamSize * 2));
        }

        var matched = matches.SelectMany(match => match).SelectMany(ticket => ticket.Members).ToList();

        Assert.That(matched, Is.Unique, "a player was seated in two lobbies at once");
        Assert.That(matched, Is.SubsetOf(queued));
        Assert.That(m.PoolSize, Is.EqualTo(PlayerCount - matched.Count), "the nine left over are one short of a lobby");
    }

    // Example Case: a ranked 5v5 queue with mixed MMR and mixed patience for skill gaps (50/100/300).
    // A gap is only crossed when both sides agree on it - one player being generous is not enough.
    [Test]
    public void RankedFiveVsFiveWithTolerance()
    {
        using var m = new Matchmaker();
        const int TeamSize = 5;

        var groups = new[]
        {
            new { Name = "bronzeLow",  Mmr = 1000, Tolerance = 50,  Gap = 0,    Seated = true,  Players = new List<string>() },
            new { Name = "bronzeHigh", Mmr = 1050, Tolerance = 50,  Gap = 0,    Seated = true,  Players = new List<string>() },
            new { Name = "silverLow",  Mmr = 2000, Tolerance = 100, Gap = 0,    Seated = true,  Players = new List<string>() },
            new { Name = "silverHigh", Mmr = 2100, Tolerance = 100, Gap = 0,    Seated = true,  Players = new List<string>() },
            new { Name = "goldLow",    Mmr = 3000, Tolerance = 300, Gap = 0,    Seated = true,  Players = new List<string>() },
            new { Name = "goldHigh",   Mmr = 3300, Tolerance = 300, Gap = 0,    Seated = true,  Players = new List<string>() },

            // reaches bronze, but bronze's 50 does not reach back
            new { Name = "generous",   Mmr = 1200, Tolerance = 300, Gap = 0,    Seated = false, Players = new List<string>() },
            // 51 apart, one point outside the window both of them named
            new { Name = "tooFarLow",  Mmr = 4000, Tolerance = 50,  Gap = 0,    Seated = false, Players = new List<string>() },
            new { Name = "tooFarHigh", Mmr = 4051, Tolerance = 50,  Gap = 0,    Seated = false, Players = new List<string>() },
            // a thousand points between each of them, so the group cannot even fill itself
            new { Name = "loner",      Mmr = 9000, Tolerance = 50,  Gap = 1000, Seated = false, Players = new List<string>() },
        };

        foreach (var group in groups)
        {
            for (int i = 1; i <= TeamSize; i++)
            {
                int skill = group.Mmr + (i - 1) * group.Gap;
                string player = $"{group.Name}{i}";
                group.Players.Add(player);

                Ticket(m, player: player, ranges: [(AtSec: 0, Min: TeamSize * 2, Max: TeamSize * 2)],
                    query:
                    $"+properties.mode:ranked +properties.skill:[{skill - group.Tolerance} TO {skill + group.Tolerance}]",
                    properties: new() { ["mode"] = "ranked", ["skill"] = skill, ["tolerance"] = group.Tolerance });
            }
        }

        var matchable = groups.Where(group => group.Seated).SelectMany(group => group.Players).ToList();
        var unmatchable = groups.Where(group => !group.Seated).SelectMany(group => group.Players).ToList();

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

        foreach (string role in roles)
        {
            for (int slot = 0; slot < 2; slot++)
            {
                Ticket(m, player: $"{role}{slot}", [(0, MatchSize, MatchSize)], query: $"+properties.mode:coop -properties.role:{role}", properties: new() { ["mode"] = "coop", ["role"] = role });
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

        MinMaxRung[] ticketSize = [(0, 5, 5)];

        // Tank
        Ticket(m, player: "p1", ticketSize, query: $"+properties.mode:roleq -properties.seat:tank", properties: new() { ["mode"] = "roleq", ["role"] = "tank", ["seat"] = "tank" });

        // DPS we create one ticket for each seat
        Ticket(m, player: "p2", ticketSize, query: $"+properties.mode:roleq -properties.seat:dps1", properties: new() { ["mode"] = "roleq", ["role"] = "dps", ["seat"] = "dps1" });
        Ticket(m, player: "p2", ticketSize, query: $"+properties.mode:roleq -properties.seat:dps2", properties: new() { ["mode"] = "roleq", ["role"] = "dps", ["seat"] = "dps2" });

        // Support same as the DPS
        Ticket(m, player: "p3", ticketSize, query: $"+properties.mode:roleq -properties.seat:support1", properties: new() { ["mode"] = "roleq", ["role"] = "support", ["seat"] = "support1" });
        Ticket(m, player: "p3", ticketSize, query: $"+properties.mode:roleq -properties.seat:support2", properties: new() { ["mode"] = "roleq", ["role"] = "support", ["seat"] = "support2" });

        Ticket(m, player: "p4", ticketSize, query: $"+properties.mode:roleq -properties.seat:support1", properties: new() { ["mode"] = "roleq", ["role"] = "support", ["seat"] = "support1" });
        Ticket(m, player: "p4", ticketSize, query: $"+properties.mode:roleq -properties.seat:support2", properties: new() { ["mode"] = "roleq", ["role"] = "support", ["seat"] = "support2" });

        var t0 = DateTime.UtcNow;

        var matches = m.RunSweep(t0);

        Assert.That(matches, Is.Empty);
        Assert.That(m.PoolSize, Is.EqualTo(7), "one tank ticket, two dps tickets and four support tickets");
        Assert.That(m.ActivePoolSize, Is.Zero);

        // Add Missing DPS
        Ticket(m, player: "p5", ticketSize, query: $"+properties.mode:roleq -properties.seat:dps1", properties: new() { ["mode"] = "roleq", ["role"] = "dps", ["seat"] = "dps1" });
        Ticket(m, player: "p5", ticketSize, query: $"+properties.mode:roleq -properties.seat:dps2", properties: new() { ["mode"] = "roleq", ["role"] = "dps", ["seat"] = "dps2" });


        // Add another Support which shouldn't be matched
        Ticket(m, player: "p6", ticketSize, query: $"+properties.mode:roleq -properties.seat:support1", properties: new() { ["mode"] = "roleq", ["role"] = "support", ["seat"] = "support1" });
        Ticket(m, player: "p6", ticketSize, query: $"+properties.mode:roleq -properties.seat:support2", properties: new() { ["mode"] = "roleq", ["role"] = "support", ["seat"] = "support2" });

        var secondSweepMatches = m.RunSweep();

        Assert.That(secondSweepMatches, Has.Count.EqualTo(1));

        var match = secondSweepMatches[0];
        Assert.Multiple(() =>
        {
            Assert.That(match.SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "p1", "p2", "p3", "p4", "p5" }));
            Assert.That(match.Select(ticket => (string)ticket.Properties["role"]), Is.EquivalentTo(new[] { "tank", "dps", "dps", "support", "support" }));
        });

        Assert.That(m.PoolSize, Is.EqualTo(2));
    }

    // Example Case (3 people co-op): players are allowed to avoid other players, these players never be in same match with people they avoid
    [Test]
    public void CoopMatchesThreePlayersWhoAvoidEachOther()
    {
        using var m = new Matchmaker();
        const int MatchSize = 3;

        var avoids = new Dictionary<string, string[]>
        {
            ["kiwi"] = ["cow", "bird"],
            ["cow"] = ["kiwi", "strawberry"],   // kiwi and cow avoid each other
            ["bird"] = ["kiwi", "strawberry"], // kiwi avoids bird, bird avoids kiwi back
            ["snake"] = ["strawberry"],
            ["strawberry"] = ["cow", "snake"],    // bird avoids strawberry, strawberry never named bird
        };

        foreach (var (player, avoided) in avoids)
        {
            string exclusions = string.Join(" ", avoided.Select(other => $"-properties.player:{other}"));

            Ticket(m, player: player, ranges: [(AtSec: 0, Min: MatchSize, Max: MatchSize)],
                query: $"+properties.mode:coop {exclusions}", properties: new() { ["mode"] = "coop", ["player"] = player });
        }

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));

        Assert.Multiple(() =>
        {
            Assert.That(matches[0].SelectMany(ticket => ticket.Members), Is.EquivalentTo(new[] { "cow", "bird", "snake" }));
            Assert.That(m.PoolSize, Is.EqualTo(2), "kiwi and strawberry do not avoid each other, but two players are one short of a match");
        });
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


        for (int i = 0; i < PlayerCount; i++)
        {
            string mode = gameModes[rng.Next(gameModes.Length)];
            Ticket(m, $"p{i}", [(AtSec: 0, Min: MatchSize, Max: MatchSize)], $"+properties.mode:{mode}", new() { ["mode"] = mode });
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

    // Example Case (backfill): a running 5v5 is down to seven players, game server queues them as one backfill ticket
    [Test]
    public void BackfillSeatsQueuedPlayersIntoARunningLobby()
    {
        using var m = new Matchmaker();
        const int LobbySize = 10;

        string[] stillPlaying = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];
        string[] newcomers = ["p8", "p9", "p10"];

        Backfill(m, partyId: "lobbyA", members: stillPlaying, ranges: [(AtSec: 0, Min: LobbySize, Max: LobbySize)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });

        Assert.That(m.RunSweep(), Is.Empty, "nobody is queued yet, the lobby waits for players");

        foreach (string player in newcomers)
        {
            Ticket(m, player: player, ranges: [(AtSec: 0, Min: LobbySize, Max: LobbySize)], query: "+properties.mode:ranked", properties: new() { ["mode"] = "ranked" });
        }

        var matches = m.RunSweep();

        Assert.That(matches, Has.Count.EqualTo(1));

        var match = matches[0];
        Assert.Multiple(() =>
        {
            Assert.That(match.SelectMany(ticket => ticket.Members), Is.EquivalentTo(stillPlaying.Concat(newcomers)));
            Assert.That(match.Count(ticket => ticket.PartyId == "lobbyA"), Is.EqualTo(1), "the lobby is seated as one ticket");
            Assert.That(m.PoolSize, Is.Zero);
        });
    }
}
