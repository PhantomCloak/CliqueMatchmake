namespace Sukhoi.Benchmarks;

sealed record Scenario(string Name, string Blurb, Func<int, Random, List<TicketSpec>> Build, MatchmakerConfig? Config = null)
{
    static readonly MatchmakerConfig Defaults = new();

    public MatchmakerConfig Settings => Config ?? Defaults;

    public static readonly Scenario[] All =
    [
        new("chess1v1",   "ranked 1v1 chess, bell-curve MMR",      BuildChess1v1),
        new("simple5v5",  "simple 5v5, no preference",         BuildSimple5v5),
        new("ranked5v5",  "ranked 5v5, mixed skill and tolerance", BuildRanked5v5),
        new("modes10",    "5v5 across 10 game modes, one pick each", BuildModes10),
        new("coop3",      "co-op role queue, 1 tank/1 dps/1 support", BuildCoop3),
        new("roleQueue5", "5v5 seat queue, 1 tank/2 dps/2 support", BuildRoleQueue5),
        new("mapOr",      "map preference, OR clause",             BuildMapOr),
        new("mapRegex",   "map preference, regex",                 BuildMapRegex),
    ];

    static List<TicketSpec> BuildChess1v1(int players, Random rng)
    {
        const int meanRating = 1500;
        const int deviation = 350;
        const int ratingFloor = 400;
        const int ratingCeiling = 3000;

        // Box-Muller. Random is flat, ladder ratings are not - they pile up around the mean.
        static double Gaussian(Random rng, double mean, double deviation)
        {
            double u1 = 1.0 - rng.NextDouble(); // in (0,1], so Log never sees zero
            double u2 = rng.NextDouble();
            return mean + deviation * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        var specs = new List<TicketSpec>(players);
        for (int i = 0; i < players; i++)
        {
            int mmr = Math.Clamp((int)Math.Round(Gaussian(rng, meanRating, deviation)), ratingFloor, ratingCeiling);

            // Pairing opens on a narrow rating window and widens the longer someone sits in the queue,
            // so a live pool is mostly tight tickets with a tail of relaxed ones. A pair only forms
            // when each player is inside the other's window - the narrower band wins.
            int roll = rng.Next(10);
            int tolerance = roll < 6 ? 100 : roll < 9 ? 200 : 400;

            specs.Add(BenchmarkHelper.RankedTicket($"p{i}", mmr, tolerance, teamSize: 1));
        }

        return specs;
    }

    static List<TicketSpec> BuildSimple5v5(int count, Random rng)
    {
        const int teamSize = 5;
        var specs = new List<TicketSpec>(count);
        for (int i = 0; i < count; i++)
        {
            specs.Add(BenchmarkHelper.Ticket($"p{i}", "*", new(), teamSize));
        }

        return specs;
    }

    static List<TicketSpec> BuildRanked5v5(int players, Random rng)
    {
        const int teamSize = 5;
        int[] tolerances = [50, 100, 300];

        var specs = new List<TicketSpec>(players);
        for (int i = 0; i < players; i++)
        {
            // Three in ten land in the top bracket
            bool top = rng.Next(10) < 3;
            int skill = top ? 2000 + rng.Next(60) * 50   // 2000..4950
                            : 1000 + rng.Next(17) * 50;  // 1000..1800

            specs.Add(BenchmarkHelper.RankedTicket($"p{i}", skill, tolerances[rng.Next(tolerances.Length)], teamSize));
        }

        return specs;
    }

    static List<TicketSpec> BuildModes10(int count, Random rng)
    {
        const int teamSize = 5;
        string[] GameModes =
        [
            "domination", "deathmatch", "ctf", "koth", "payload", "escort", "hardpoint", "control", "breakthrough", "gunGame",
        ];

        var specs = new List<TicketSpec>(count);
        for (int i = 0; i < count; i++)
        {
            string mode = GameModes[rng.Next(GameModes.Length)];

            specs.Add(BenchmarkHelper.Ticket(
                $"p{i}",
                $"+properties.mode:{mode}",
                new Dictionary<string, object> { ["mode"] = mode },
                teamSize * 2));
        }

        return specs;
    }

    static List<TicketSpec> BuildCoop3(int players, Random rng)
    {
        string[] roles = ["tank", "dps", "support"];

        var specs = new List<TicketSpec>(players);
        for (int i = 0; i < players; i++)
        {
            string role = roles[i % roles.Length];

            specs.Add(BenchmarkHelper.Ticket(
                $"p{i}",
                $"+properties.mode:coop -properties.role:{role}",
                new Dictionary<string, object> { ["mode"] = "coop", ["role"] = role },
                3));
        }

        return specs;
    }

    static List<TicketSpec> BuildRoleQueue5(int players, Random rng)
    {
        const string mode = "roleq";
        const int teamSize = 5;

        string[] roleByIndex = ["tank", "dps", "dps", "support", "support"];

        static string[] SeatsFor(string role) => role switch
        {
            "tank" => ["tank"],
            "dps" => ["dps1", "dps2"],
            _ => ["support1", "support2"],
        };

        var specs = new List<TicketSpec>(players);
        for (int i = 0; i < players; i++)
        {
            string role = roleByIndex[i % roleByIndex.Length];

            foreach (string seat in SeatsFor(role))
            {
                specs.Add(BenchmarkHelper.Ticket(
                    $"p{i}",
                    $"+properties.mode:{mode} -properties.seat:{seat}",
                    new Dictionary<string, object>
                    {
                        ["mode"] = mode,
                        ["role"] = role,
                        ["seat"] = seat,
                    },
                    teamSize));
            }
        }

        return specs;
    }

    static List<TicketSpec> BuildMapOr(int count, Random rng)
    {
        string[] MapPool = ["dust2", "inferno", "mirage", "nuke", "overpass", "ancient", "anubis"];
        const int MatchSize = 4;

        var specs = new List<TicketSpec>(count);
        for (int i = 0; i < count; i++)
        {
            // ~40% will play anything, the rest name one to three maps
            string[] maps = rng.Next(10) < 4
                ? MapPool
                : [.. MapPool.OrderBy(_ => rng.Next()).Take(1 + rng.Next(3))];

            var properties = new Dictionary<string, object> { ["mode"] = "comp" };
            foreach (string map in maps)
            {
                properties[$"map_{map}"] = true;
            }

            string wantedMaps = string.Join(" OR ", maps.Select(map => $"properties.map_{map}:T"));

            specs.Add(BenchmarkHelper.Ticket(
                $"p{i}",
                $"+properties.mode:comp +({wantedMaps})",
                properties,
                MatchSize));
        }

        return specs;
    }

    static List<TicketSpec> BuildMapRegex(int players, Random rng)
    {
        string[] defusalMaps = ["de_dust2", "de_inferno", "de_mirage", "de_nuke"];
        string[] hostageMaps = ["cs_office", "cs_italy"];
        const int MatchSize = 4;

        var specs = new List<TicketSpec>(players);
        for (int i = 0; i < players; i++)
        {
            int roll = rng.Next(10);

            // ~30% will play anything, ~30% any defusal map, ~40% only the map they are on
            string map = roll < 3 ? "any"
                : roll < 9 ? defusalMaps[rng.Next(defusalMaps.Length)]
                : hostageMaps[rng.Next(hostageMaps.Length)];

            string willingToPlay = roll < 3 ? ".*"
                : roll < 6 ? "de_.*|any"
                : $"{map}|any";

            specs.Add(BenchmarkHelper.Ticket(
                $"p{i}",
                $"+properties.mode:comp +properties.map:/{willingToPlay}/",
                new Dictionary<string, object> { ["mode"] = "comp", ["map"] = map },
                MatchSize));
        }

        return specs;
    }

}
