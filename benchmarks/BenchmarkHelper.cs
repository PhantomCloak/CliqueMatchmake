namespace Sukhoi.Benchmarks;

sealed record TicketSpec(string[] Members, string Owner, string PartyId, QueryRung[] Queries, Dictionary<string, object> Properties, MinMaxRung[] Ranges, int CountMultiple = 1);

readonly record struct Sample(double Ms, long Bytes, string Note);

sealed record Options(int[] Sizes, int Iters, Scenario[] Selected)
{
    public static bool TryParse(string[] args, out Options options, out string msg)
    {
        options = null!;
        msg = "";

        int[] sizes = [100, 500, 1000];
        int iters = 3;

        foreach (var arg in args)
        {
            if (arg == "--bench")
            {
                continue;
            }

            var parts = arg.Split('=', 2);
            string key = parts[0];
            string value = parts.Length > 1 ? parts[1] : "";

            switch (key)
            {
                case "--sizes" when TrySizes(value, out sizes):
                case "--iters" when int.TryParse(value, out iters):
                    break;

                case "--sizes":
                case "--iters":
                    msg = $"bad value for {key}: '{value}'";
                    return false;

                default:
                    msg = $"unknown option: {arg}";
                    return false;
            }
        }

        if (sizes.Length == 0 || sizes.Any(s => s < 2) || iters < 1)
        {
            msg = "sizes must be >= 2 and iters >= 1";
            return false;
        }

        options = new Options(sizes, iters, Scenario.All);
        return true;
    }

    static bool TrySizes(string value, out int[] numbers)
    {
        numbers = [];

        var parts = value.Split(',');
        var parsed = new int[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int number))
            {
                return false;
            }

            parsed[i] = number;
        }

        numbers = parsed;
        return true;
    }
}

static class BenchmarkHelper
{
    public static TicketSpec Ticket(string player, string query, Dictionary<string, object> properties, int size) =>
        new([player], player, "", [(0, query)], properties, [(0, size, size)]);

    static string SkillWindow(int skill, int tolerance) =>
        $"+properties.mode:ranked +properties.skill:[{skill - tolerance} TO {skill + tolerance}]";

    public static TicketSpec RankedTicket(string player, int skill, int tolerance, int teamSize) =>
        Ticket(
            player,
            SkillWindow(skill, tolerance),
            new Dictionary<string, object>
            {
                ["mode"] = "ranked",
                ["skill"] = skill,
                ["tolerance"] = tolerance,
            },
            teamSize * 2);

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };
};
