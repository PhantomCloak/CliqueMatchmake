using System.Diagnostics;

namespace CliqueMatchmaker.Benchmarks;

static class Benchmark
{
    const string Usage = """
        usage: dotnet run -c Release --project benchmarks -- [options]
          --sizes=100,500,1000    player counts to measure (default 100,500,1000)
          --iters=3               repetitions per measurement (default 3)
          --help                  this text
        """;

    static readonly TimeSpan WarmupBudget = TimeSpan.FromSeconds(2);
    const int Seed = 1234;

    public static int Run(string[] args)
    {
        if (args.Any(arg => arg is "--help" or "-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (!Options.TryParse(args, out var options, out var errMsg))
        {
            Console.WriteLine(errMsg);
            return 2;
        }

        Console.WriteLine($"CliqueMatchmaker benchmark - {Environment.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")}, {Environment.ProcessorCount} cores, serverGC={System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"sizes={string.Join(",", options.Sizes)} iters={options.Iters}");

        if (Debugger.IsAttached)
        {
            Console.WriteLine("WARNING: debugger attached, timings are not representative");
        }
#if DEBUG
        Console.WriteLine("WARNING: DEBUG build, timings are not representative - use 'dotnet run -c Release --project benchmarks'");
#endif
        Warmup();

        var total = Stopwatch.StartNew();
        foreach (var scenario in options.Selected)
        {
            foreach (int playerSize in options.Sizes)
            {
                var specs = scenario.Build(playerSize, new Random(Seed));

                Console.WriteLine();
                Console.WriteLine($"scenario={scenario.Name} players={playerSize} tickets={specs.Count}  ({scenario.Blurb})");
                Console.WriteLine($"  {"op",-8} {"min ms",9} {"med ms",9} {"max ms",9} {"tickets/s",12} {"alloc",10}  note");

                Report("add", specs.Count, options.Iters, () => BenchAdd(specs, scenario.Settings));
                Report("sweep", specs.Count, options.Iters, () => BenchSweep(specs, scenario.Settings));
            }
        }
        total.Stop();

        Console.WriteLine();
        Console.WriteLine($"done in {total.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    static Sample BenchAdd(List<TicketSpec> specs, MatchmakerConfig config)
    {
        using var matchmaker = new Matchmaker(config);

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        foreach (var spec in specs)
        {
            matchmaker.Add(
                sessionIds: new HashSet<string>(spec.Members),
                ownerSessionId: spec.Owner,
                partyId: spec.PartyId,
                queryLadder: spec.Queries,
                properties: spec.Properties,
                minMaxLadder: spec.Ranges,
                countMultiple: spec.CountMultiple);
        }
        sw.Stop();

        return new(sw.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - before, $"pool={matchmaker.PoolSize}");
    }


    static Sample BenchSweep(List<TicketSpec> specs, MatchmakerConfig config)
    {
        var matchmaker = new Matchmaker(config);

        foreach (var spec in specs)
        {
            matchmaker.Add(
                        sessionIds: new HashSet<string>(spec.Members),
                        ownerSessionId: spec.Owner,
                        partyId: spec.PartyId,
                        queryLadder: spec.Queries,
                        properties: spec.Properties,
                        minMaxLadder: spec.Ranges,
                        countMultiple: spec.CountMultiple);
        }

        DateTime spent = DateTime.UtcNow.AddSeconds(config.MaxTicketPatienceInSec);

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var matches = matchmaker.RunSweep(spent);
        sw.Stop();

        int matchedTickets = matches.Sum(m => m.Count);
        int seats = matches.Sum(m => m.Sum(t => t.Size));

        return new(sw.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - before, $"{matches.Count} matches, {seats} seats, {matchedTickets}/{specs.Count} tickets");
    }

    static void Report(string op, int ticketCount, int iters, Func<Sample> measure)
    {
        var samples = new Sample[iters];
        for (int i = 0; i < iters; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            samples[i] = measure();
        }

        var times = samples.Select(s => s.Ms).Order().ToArray();
        double minMs = times[0];
        double medianMs = times[times.Length / 2];
        double maxMs = times[^1];
        double perSecond = medianMs > 0 ? ticketCount / (medianMs / 1000.0) : 0;

        Console.WriteLine($"  {op,-8} {minMs,9:F2} {medianMs,9:F2} {maxMs,9:F2} {perSecond,12:N0} {BenchmarkHelper.FormatBytes(samples[^1].Bytes),10}  {samples[^1].Note}");
    }

    static void Warmup()
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < WarmupBudget)
        {
            foreach (var scenario in Scenario.All)
            {
                var specs = scenario.Build(64, new Random(1));

                BenchAdd(specs, scenario.Settings);
                BenchSweep(specs, scenario.Settings);
            }
        }
    } 
}
