using System.Diagnostics;
using ImmersiveVoice;

internal static class SquadBattleTacticsScenarios
{
    private static SquadPoint P(float x, float z = 0, float y = 0) => new(x, y, z);
    private static SquadMemory Team(int peers = 1)
    {
        var memory = new SquadMemory();
        for (int i = 0; i < peers; i++) memory.Bind(i);
        memory.Combat(true);
        memory.Threat("enemy", P(40), 0);
        return memory;
    }
    private static void PeerAdvances(SquadMemory memory, float end = 4f)
    {
        for (float t = 0; t <= end; t += .25f)
            memory.Sample(0, t, P(0), P(4 + t * 2), true);
    }
    private static void LocalAdvances(SquadMemory memory, float end = 4f)
    {
        for (float t = 0; t <= end; t += .25f)
            memory.Sample(0, t, P(t * 2), P(12, 4), true);
    }
    private static void Cluster(SquadMemory memory, float now, int peers)
    {
        for (int i = 0; i < peers; i++) memory.Sample(i, now, P(0), P(2 + i), true);
    }

    public static void Run(Action<bool, string> check)
    {
        var memory = Team(); PeerAdvances(memory);
        memory.Shot(4f, "enemy"); memory.Shot(4.3f, "enemy");
        var decision = memory.Decide(4.4f, true, true, true);
        check(decision.Action == SquadAction.PushNow && decision.Slot == 0 && memory.Valid(decision, 4.4f, true),
            "two real local shots at the known threat plus a teammate's sustained push support one Gogogo event");
        check(!memory.Decide(4.5f, true, true, true).Exists,
            "one fire-and-push episode is consumed even when its exact voice bank is absent");

        foreach (string scenario in new[] { "one-shot", "wrong-target", "no-threat", "stationary-peer", "lateral-peer" })
        {
            memory = Team();
            if (scenario == "stationary-peer")
                for (float t = 0; t <= 4; t += .25f) memory.Sample(0, t, P(0), P(4), true);
            else if (scenario == "lateral-peer")
                for (float t = 0; t <= 4; t += .25f) memory.Sample(0, t, P(0), P(4, t * 2), true);
            else PeerAdvances(memory);
            if (scenario == "no-threat") memory.TargetDown("enemy");
            memory.Shot(4f, scenario == "wrong-target" ? "other" : "enemy");
            if (scenario != "one-shot") memory.Shot(4.3f, scenario == "wrong-target" ? "other" : "enemy");
            check(memory.Decide(4.4f, true, true, true).Action != SquadAction.PushNow,
                scenario + " cannot manufacture a Gogogo fire-and-movement call");
        }

        memory = Team(); LocalAdvances(memory);
        check(memory.PeerShot(0, 4f, "enemy") && memory.PeerShot(0, 4.3f, "enemy"),
            "observed positive firearm damage from a perceived peer records bounded suppression evidence");
        decision = memory.Decide(4.4f, true, true, true);
        check(decision.Action == SquadAction.Suppressing && memory.Valid(decision, 4.4f, true),
            "peer fire at the known threat plus the local player's sustained push supports one Suppress event");
        memory.PeerShot(0, 4.45f, "other");
        check(!memory.Valid(decision, 4.45f, true),
            "an observed peer shot at another target cancels earlier suppression evidence");
        memory = Team(); LocalAdvances(memory); memory.PeerShot(0, 4f, "enemy"); memory.PeerShot(0, 4.3f, "enemy");
        decision = memory.Decide(4.4f, true, true, true);
        memory.TargetDown("enemy");
        check(!memory.Valid(decision, 4.5f, true), "losing the known live threat cancels queued suppression speech");

        foreach (string scenario in new[] { "one-peer-shot", "unknown-target", "local-still", "peer-lost" })
        {
            memory = Team();
            if (scenario == "local-still")
                for (float t = 0; t <= 4; t += .25f) memory.Sample(0, t, P(0), P(4), true);
            else LocalAdvances(memory);
            string target = scenario == "unknown-target" ? "other" : "enemy";
            bool first = memory.PeerShot(0, 4f, target);
            bool second = scenario == "one-peer-shot" ? false : memory.PeerShot(0, 4.3f, target);
            if (scenario == "peer-lost") memory.Sample(0, 4.35f, P(8.7f), P(12, 4), false);
            check(memory.Decide(4.4f, true, true, true).Action != SquadAction.Suppressing
                && (scenario != "unknown-target" || !first && !second),
                scenario + " cannot manufacture a Suppress call");
        }

        memory = Team(3); Cluster(memory, 0, 3);
        check(memory.Hit(1, .2f, "enemy"), "a perceived teammate hit records actual emergency evidence");
        Cluster(memory, .4f, 3);
        decision = memory.Decide(.5f, true, true, true);
        check(decision.Action == SquadAction.GetInCover && decision.Slot == 1 && memory.Valid(decision, .5f, true),
            "the actually hit teammate is selected for GetInCover ahead of a generic formation warning");
        check(!memory.Decide(.6f, true, true, true).Exists,
            "one hostile-impact episode cannot rotate an emergency call across the other squad members");

        memory = Team(3); Cluster(memory, 0, 3);
        check(memory.Danger(.2f, "enemy"), "an actual local hostile impact can open a bounded danger episode");
        Cluster(memory, .4f, 3);
        decision = memory.Decide(.5f, true, true, true);
        check(decision.Action == SquadAction.SpreadOut && decision.Slot == 0 && memory.Valid(decision, .5f, true),
            "actual danger while two perceived teammates crowd the local player supports one Spreadout call");
        memory.TargetDown("enemy");
        check(!memory.Valid(decision, .6f, true), "a resolved or replaced threat cancels a queued formation warning");

        memory = Team(); Cluster(memory, 0, 1);
        check(memory.Danger(.2f, "enemy") && !memory.Decide(.3f, true, true, true).Exists,
            "a two-person pair is not mislabeled as a crowded four-player formation");
        memory = Team(3); Cluster(memory, 0, 3);
        check(!memory.Danger(.2f, "unseen") && !memory.Decide(.3f, true, true, true).Exists,
            "combat state and an unknown attacker alone cannot fabricate an emergency command");
        check(memory.Danger(.4f, "enemy") && !memory.Decide(3.66f, true, true, true).Exists,
            "unhandled danger expires instead of becoming a late voice line");

        memory = Team(3); Cluster(memory, 0, 3); memory.Danger(.2f, "enemy"); Cluster(memory, .4f, 3);
        decision = memory.Decide(.5f, true, true, true);
        check(decision.Exists && decision.Slot == 0,
            "a four-human client evaluates three fixed relationships and selects one strongest emergency target");

        // Pure model cost only. This exercises all three fixed slots without
        // scene queries, network broadcasts, LINQ or per-tick allocations.
        memory = Team(3); Cluster(memory, 0, 3);
        for (int i = 1; i <= 40; i++) Cluster(memory, i * .25f, 3);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        long watchBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        for (int i = 41; i < 10041; i++)
        {
            float now = i * .25f;
            memory.Threat("enemy", P(40), now);
            Cluster(memory, now, 3);
            memory.Danger(now, "enemy");
            memory.Decide(now, true, true, true);
        }
        watch.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated - watchBytes;
        check(bytes == 0, "30000 formation samples and 10000 emergency decisions allocate zero managed bytes");
        Console.WriteLine($"SQUAD BATTLE BENCHMARK: 10000 four-player decisions in {watch.Elapsed.TotalMilliseconds:F2} ms, managed allocation {bytes} bytes; pure model only.");
    }
}
