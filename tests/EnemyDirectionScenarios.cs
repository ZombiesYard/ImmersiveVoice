using ImmersiveVoice;
using System.Diagnostics;

internal static class EnemyDirectionScenarios
{
    private static SquadPoint P(float x, float z) => new(x, 0, z);
    private static EnemyDirectionDecision Seen(EnemyDirectionMemory memory, string id, SquadPoint facing,
        SquadPoint target, float at)
    {
        memory.Observe(id, P(0, 0), facing, target, at, true);
        memory.Observe(id, P(0, 0), facing, target, at + EnemyDirectionMemory.StableTime, true);
        return memory.Select(id, at + EnemyDirectionMemory.StableTime);
    }

    public static void Run(Action<bool, string> check)
    {
        var facing = P(0, 1);
        check(EnemyDirectionMemory.Classify(P(0, 0), facing, P(0, 10)) == EnemyDirection.Front
            && EnemyDirectionMemory.Classify(P(0, 0), facing, P(0, -10)) == EnemyDirection.Rear
            && EnemyDirectionMemory.Classify(P(0, 0), facing, P(-10, 0)) == EnemyDirection.Left
            && EnemyDirectionMemory.Classify(P(0, 0), facing, P(10, 0)) == EnemyDirection.Right,
            "speaker facing maps a visible hostile to one of four tactical directions");
        check(EnemyDirectionMemory.Classify(P(0, 0), P(1, 0), P(0, 10)) == EnemyDirection.Left,
            "direction rotates with the speaker instead of using world axes");
        check(EnemyDirectionMemory.Phrase(EnemyDirection.Left) == 69
            && EnemyDirectionMemory.Phrase(EnemyDirection.Right) == 72
            && EnemyDirectionMemory.Phrase(EnemyDirection.Front) == 73
            && EnemyDirectionMemory.Phrase(EnemyDirection.Rear) == 74,
            "direction choices map only to the four exact native phrase IDs");

        double R(double degrees) => degrees * Math.PI / 180d;
        SquadPoint At(double degrees) => P((float)Math.Sin(R(degrees)) * 10f, (float)Math.Cos(R(degrees)) * 10f);
        check(EnemyDirectionMemory.Classify(P(0, 0), facing, At(39)) == EnemyDirection.Front
            && EnemyDirectionMemory.Classify(P(0, 0), facing, At(-39)) == EnemyDirection.Front,
            "confident positions adjacent to the front cone remain deterministic");
        check(EnemyDirectionMemory.Classify(P(0, 0), facing, At(45)) == EnemyDirection.None
            && EnemyDirectionMemory.Classify(P(0, 0), facing, At(-45)) == EnemyDirection.None
            && EnemyDirectionMemory.Classify(P(0, 0), facing, At(135)) == EnemyDirection.None,
            "ambiguous front/flank and flank/rear boundary angles stay silent");
        check(EnemyDirectionMemory.Classify(P(0, 0), facing, At(51)) == EnemyDirection.Right
            && EnemyDirectionMemory.Classify(P(0, 0), facing, At(-51)) == EnemyDirection.Left
            && EnemyDirectionMemory.Classify(P(0, 0), facing, At(141)) == EnemyDirection.Rear,
            "positions beyond the boundary dead bands select the expected flank or rear");
        check(EnemyDirectionMemory.Classify(P(0, 0), P(0, 0), P(0, 10)) == EnemyDirection.None
            && EnemyDirectionMemory.Classify(P(0, 0), facing, P(.1f, .1f)) == EnemyDirection.None
            && EnemyDirectionMemory.Classify(P(float.NaN, 0), facing, P(0, 10)) == EnemyDirection.None,
            "invalid facing, near-overlap, and non-finite geometry cannot invent a direction");

        var m = new EnemyDirectionMemory();
        m.Observe("front", P(0, 0), facing, P(0, 10), 0, true);
        check(!m.Select("front", .19f).Exists, "a single frame cannot become a directional report");
        m.Observe("front", P(0, 0), facing, P(0, 10), .2f, true);
        var decision = m.Select("front", .2f);
        check(decision.Direction == EnemyDirection.Front && m.Valid(decision, .3f),
            "a stable ray-confirmed observation creates one valid directional report");
        check(!m.Select("front", .3f).Exists, "one observation can select only one direction category");
        m.Observe("front", P(0, 0), facing, P(-10, 0), .4f, true);
        check(!m.Valid(decision, .4f) && !m.Select("front", .7f).Exists,
            "turning or target motion invalidates queued geometry without substituting another category");

        m = new EnemyDirectionMemory();
        decision = Seen(m, "edge", facing, P(0, 10), 0);
        m.Observe("edge", P(0, 0), facing, At(45), .3f, true);
        check(!m.Valid(decision, .3f), "moving into an ambiguous angular boundary cancels stale directional speech");
        m = new EnemyDirectionMemory();
        decision = Seen(m, "stale", facing, P(0, 10), 0);
        check(!m.Valid(decision, 1.01f), "a direction expires when local visual evidence is no longer fresh");
        m.Clear();
        check(!m.Valid(decision, .3f), "raid or menu suspension clears queued directional evidence");

        m = new EnemyDirectionMemory();
        check(Seen(m, "a", facing, P(0, 10), 0).Exists, "first stable target can report");
        check(!Seen(m, "b", facing, P(10, 0), 1).Exists,
            "another visible target cannot create rapid multi-direction chatter");
        check(Seen(m, "c", facing, P(-10, 0), 8.1f).Exists,
            "a different target can report after the bounded squad information gap");
        m.Observe("a", P(0, 0), facing, P(0, 10), 21, true);
        m.Observe("a", P(0, 0), facing, P(0, 10), 21.2f, true);
        check(m.Select("a", 21.2f).Exists, "a genuinely reacquired target can report again after its repeat limit");

        m = new EnemyDirectionMemory();
        for (int i = 0; i < EnemyDirectionMemory.Capacity; i++)
            m.Observe("target-" + i, P(0, 0), facing, P(0, 10 + i), i, true);
        m.Observe("replacement", P(0, 0), facing, P(0, 20), 5, true);
        check(!m.Select("target-0", 5).Exists, "fixed four-target memory evicts the oldest local observation");

        var four = new[] { new EnemyDirectionMemory(), new EnemyDirectionMemory(), new EnemyDirectionMemory(), new EnemyDirectionMemory() };
        for (int i = 0; i < four.Length; i++) four[i].Observe("enemy", P(0, 0), facing, P(0, 10), 0, true);
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 100000; i++)
            for (int n = 0; n < four.Length; n++) four[n].Observe("enemy", P(0, 0), facing, P(0, 10), .1f, true);
        long elapsed = Stopwatch.GetTimestamp() - start;
        check(GC.GetAllocatedBytesForCurrentThread() == allocated,
            "four direction memories allocate zero managed bytes across 100,000 updates");
        Console.WriteLine($"Direction benchmark: 100,000 four-player updates in {elapsed * 1000d / Stopwatch.Frequency:F2} ms (model only).");
    }
}
