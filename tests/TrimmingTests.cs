using Lucene.Net.Search;

namespace Sukhoi.Tests;

public class TrimmingTests
{
    // Case: several subsets sum to the overshoot and the newest of them is the one returned. Both
    // {solo, duo} and {trio} sum to 3 here; trio queued last, so trio is the group given up.
    [Test]
    public void PicksTheNewestSubsetSummingToTheOvershoot()
    {
        var tickets = new[]
        {
            Ticket("solo", size: 1, createdAt: 100),
            Ticket("duo", size: 2, createdAt: 200),
            Ticket("trio", size: 3, createdAt: 300),
        };

        var group = Matchmaker.FindNewestGroupSummingTo(tickets, required: 3);

        Assert.That(group, Is.Not.Null);
        Assert.That(Ids(group!), Is.EquivalentTo(new[] { "trio" }));
    }

    [Test]
    public void SkipsTicketsBiggerThanTheOvershoot()
    {
        var tickets = new[]
        {
            Ticket("party", size: 5, createdAt: 100),
            Ticket("duo", size: 2, createdAt: 200),
            Ticket("solo", size: 1, createdAt: 300),
        };

        var group = Matchmaker.FindNewestGroupSummingTo(tickets, required: 3);

        Assert.That(group, Is.Not.Null);
        Assert.That(Ids(group!), Is.EquivalentTo(new[] { "duo", "solo" }), "party alone overshoots, so the only group that sums to 3 is the other two");
    }

    [Test]
    public void FindsNothingWhenNoSubsetFitsExactly()
    {
        var tickets = new[]
        {
            Ticket("duoA", size: 2, createdAt: 100),
            Ticket("duoB", size: 2, createdAt: 200),
        };

        Assert.That(Matchmaker.FindNewestGroupSummingTo(tickets, required: 3), Is.Null);
    }

    [Test]
    public void FindsNothingWhenThereIsNoOvershoot()
    {
        var tickets = new[] { Ticket("solo", size: 1, createdAt: 100) };

        Assert.That(Matchmaker.FindNewestGroupSummingTo(tickets, required: 0), Is.Null);
        Assert.That(Matchmaker.FindNewestGroupSummingTo(tickets, required: -1), Is.Null);
    }

    // Case: "newest" is the mean CreatedAt across the whole group, not its youngest member. Both
    // groups below sum to 3: {solo, duo} holds the newest single ticket at 900 but averages 500,
    // and trio at 600 is newer as a group - so ranking on the member alone would pick the wrong one.
    [Test]
    public void RanksAGroupOnItsMeanCreatedAtNotItsNewestMember()
    {
        var tickets = new[]
        {
            Ticket("solo", size: 1, createdAt: 100),
            Ticket("duo", size: 2, createdAt: 900),
            Ticket("trio", size: 3, createdAt: 600),
        };

        var group = Matchmaker.FindNewestGroupSummingTo(tickets, required: 3);

        Assert.That(group, Is.Not.Null);
        Assert.That(Ids(group!), Is.EquivalentTo(new[] { "trio" }));
    }

    [Test]
    public void TrimsTheNewestGroupAndKeepsTheLongestWaiting()
    {
        var seed = Ticket("seed", size: 1, createdAt: 50);
        seed.CountMultiple = 2;

        var oldest = Ticket("oldest", size: 1, createdAt: 100);
        var newest = Ticket("newest", size: 1, createdAt: 900);
        var party = Ticket("party", size: 4, createdAt: 500);
        var combo = new List<MatchmakerTicket> { oldest, newest, party };

        var trimmed = Matchmaker.TrimToCountMultiple(seed, combo, matchSize: 7);

        Assert.That(trimmed, Is.Not.Null);
        Assert.That(Ids(trimmed!), Is.EquivalentTo(new[] { "oldest", "party" }));
    }

    [Test]
    public void RejectsAMatchTheTrimShrinksBelowTheSeedsMinimum()
    {
        var seed = Ticket("seed", size: 2, createdAt: 50, minCount: 6, maxCount: 12);
        seed.CountMultiple = 4;

        var combo = new List<MatchmakerTicket>
        {
            Ticket("duoA", size: 2, createdAt: 100, minCount: 4, maxCount: 8),
            Ticket("duoB", size: 2, createdAt: 200, minCount: 4, maxCount: 8),
        };

        Assert.That(Matchmaker.TryFinalizeCombo(seed, combo), Is.Null, "the seed queued for 6 or more and the trim left it a lobby of 4");
    }

    [Test]
    public void KeepsAMatchTheTrimShrinksNoFurtherThanTheSeedsMinimum()
    {
        var seed = Ticket("seed", size: 2, createdAt: 50, minCount: 4, maxCount: 8);
        seed.CountMultiple = 4;

        var combo = new List<MatchmakerTicket>
        {
            Ticket("duoA", size: 2, createdAt: 100, minCount: 4, maxCount: 8),
            Ticket("duoB", size: 2, createdAt: 200, minCount: 4, maxCount: 8),
        };

        var match = Matchmaker.TryFinalizeCombo(seed, combo);

        Assert.That(match, Is.Not.Null);
        Assert.That(match!.Sum(ticket => ticket.Size), Is.EqualTo(4));
        Assert.That(Ids(match), Is.EquivalentTo(new[] { "duoA", "seed" }));
    }

    [Test]
    public void KeepsEveryCandidateTheSettledSizeSuits()
    {
        var seed = Ticket("seed", size: 1, createdAt: 50, minCount: 2, maxCount: 4);

        var combo = new List<MatchmakerTicket>
        {
            Ticket("a", size: 1, createdAt: 100, minCount: 2, maxCount: 4),
            Ticket("b", size: 1, createdAt: 200, minCount: 2, maxCount: 4),
        };

        var seatable = Matchmaker.TrimToSeatable(seed, combo);

        Assert.That(seatable, Is.Not.Null);
        Assert.That(Ids(seatable!), Is.EquivalentTo(new[] { "a", "b" }));
    }

    // Case: the settled size has to suit every candidate, and TrimToSeatable tests both ends of a
    // candidate's range in the one expression - `x.MinCount > size || x.MaxCount < size`. A floor
    // above the lobby and a ceiling below it are the two sides of it.
    [Test]
    public void DropsTheCandidateTheSettledSizeCannotSeat()
    {
        var seed = Ticket("seed", size: 1, createdAt: 50, minCount: 2, maxCount: 4);

        var floorAboveTheLobby = new List<MatchmakerTicket>
        {
            Ticket("strict", size: 1, createdAt: 100, minCount: 4, maxCount: 4),
            Ticket("easy", size: 1, createdAt: 200, minCount: 2, maxCount: 4),
        };

        var seatable = Matchmaker.TrimToSeatable(seed, floorAboveTheLobby);

        Assert.That(seatable, Is.Not.Null);
        Assert.That(Ids(seatable!), Is.EquivalentTo(new[] { "easy" }), "the lobby settled at 3 and strict queued for 4");

        // the other end of the same expression: a candidate the lobby has outgrown
        var ceilingBelowTheLobby = new List<MatchmakerTicket>
        {
            Ticket("capped", size: 1, createdAt: 100, minCount: 2, maxCount: 2),
            Ticket("easy", size: 1, createdAt: 200, minCount: 2, maxCount: 4),
        };

        var outgrown = Matchmaker.TrimToSeatable(seed, ceilingBelowTheLobby);

        Assert.That(outgrown, Is.Not.Null);
        Assert.That(Ids(outgrown!), Is.EquivalentTo(new[] { "easy" }), "the lobby settled at 3 and capped queued for at most 2");
    }

    [Test]
    public void RejectsAComboTheDropsShrinkBelowTheSeedsMinimum()
    {
        var seed = Ticket("seed", size: 1, createdAt: 50, minCount: 3, maxCount: 6);

        var combo = new List<MatchmakerTicket>
        {
            Ticket("strictA", size: 1, createdAt: 100, minCount: 5, maxCount: 6),
            Ticket("strictB", size: 1, createdAt: 200, minCount: 5, maxCount: 6),
        };

        Assert.That(Matchmaker.TrimToSeatable(seed, combo), Is.Null,
            "both candidates queued for 5 or more, and dropping either leaves the seed a lobby of 2");
    }

    // Case: the drops can empty the combo, and then nothing is seated - TrimToSeatable returns null
    // rather than letting FinalizeCombo seat a lone seed (Matchmaker.cs:563). This is a different
    // branch from the one above: there the survivors leave the seed below its own minimum, here
    // there are no survivors at all.
    //
    // UNSEATABLE-CANDIDATE.md:63-65 credits FullPartyWithRoomToGrowDoesNotMatchOnItsOwn with pinning
    // this, but that test never acquires a candidate, so the branch never ran there.
    [Test]
    public void RejectsAComboWhoseOnlyCandidateTheDropsRemove()
    {
        var seed = Ticket("seed", size: 1, createdAt: 50, minCount: 2, maxCount: 4);

        var combo = new List<MatchmakerTicket>
        {
            Ticket("strict", size: 1, createdAt: 100, minCount: 4, maxCount: 4),
        };

        Assert.That(Matchmaker.TrimToSeatable(seed, combo), Is.Null,
            "the seed's only candidate was dropped, and a seed is never seated alone");
    }

    static string[] Ids(List<MatchmakerTicket> group) => group.Select(ticket => ticket.Ticket).ToArray();

    static MatchmakerTicket Ticket(string id, int size, long createdAt, int minCount = 0, int maxCount = 0) => new()
    {
        Ticket = id,
        OwningSessionId = id,
        PartyId = "",
        Size = size,
        CreatedAt = createdAt,
        QueryLadder = [(0, "*", new MatchAllDocsQuery())],
        Properties = new Dictionary<string, object>(),
        // the range is the one in force, written straight down rather than scheduled - these tests
        // hand a ticket to the trims directly and never sweep, so no rung is ever computed
        MinMaxLadder = [(0, minCount, maxCount)],
    };
}
