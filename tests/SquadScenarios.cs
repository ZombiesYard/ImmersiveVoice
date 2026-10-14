using System.Diagnostics;
using ImmersiveVoice;

internal static class SquadScenarios
{
    private static SquadPoint P(float x, float z = 0, float y = 0) => new(x, y, z);
    private static SquadMemory Team(int peers = 1)
    {
        var m = new SquadMemory();
        for (int i = 0; i < peers; i++) m.Bind(i);
        return m;
    }
    private static void Follow(SquadMemory m, float start = 0, float end = 4, int peers = 1)
    {
        for (float t = start; t <= end; t += 0.25f)
            for (int i = 0; i < peers; i++) m.Sample(i, t, P(t * 2), P(t * 2 + 4 + i * 2), true);
    }
    private static void Still(SquadMemory m, float now, int peers = 1)
    {
        for (int i = 0; i < peers; i++) m.Sample(i, now, P(0), P(4 + i), true);
    }
    public static void Run(Action<bool, string> check)
    {
        var m = Team(); Follow(m);
        var d = m.Decide(4, true, true, true);
        check(d.Exists && d.Action == SquadAction.Following && !d.Reply,
            "sustained close movement behind a peer supports a movement expression without pretending to hear a command");
        check(m.Valid(d, 4, true), "fresh following decision remains valid for the same partner and episode");
        Follow(m, 4.25f, 40);
        check(!m.Decide(40, true, true, true).Exists, "one long following episode never becomes a periodic monologue");
        check(!m.Valid(d, 41, true), "stale observations cannot keep a following line alive");
        m = Team(); Follow(m, end: 2);
        check(!m.Decide(2, true, true, true).Exists, "a moment of aligned movement is insufficient");

        foreach (string scenario in new[] { "parallel", "opposite", "ahead", "stationary", "too-far", "other-floor" })
        {
            m = Team();
            for (float t = 0; t <= 5; t += 0.25f)
            {
                var self = P(t * 2);
                var other = scenario switch
                {
                    "parallel" => P(t * 2, 4),
                    "opposite" => P(10 - t * 2),
                    "ahead" => P(t * 2 - 4),
                    "stationary" => P(8),
                    "too-far" => P(t * 2 + 25),
                    _ => P(t * 2 + 4, y: 4)
                };
                m.Sample(0, t, self, other, true);
            }
            var action = m.Decide(5, true, true, true);
            check(scenario == "ahead" ? action.Action == SquadAction.Leading : !action.Exists,
                scenario + " does not masquerade as following (a sustained leader may now invite FollowMe)");
        }
        m = Team(); Follow(m); d = m.Decide(4, true, true, true);
        m.Sample(0, 4.25f, P(8.5f), P(12.5f), false);
        check(!m.Valid(d, 4.25f, true), "loss of local perception immediately invalidates queued following");
        Follow(m, 4.5f, 8);
        check(!m.Decide(8, true, true, true).Exists, "a brief visibility gap does not re-arm the same following decision");
        m.Sample(0, 9, P(18), P(22), false); Follow(m, 30, 34);
        check(m.Decide(34, true, true, true).Exists, "a genuinely separated later following episode may react again");

        m = Team(); Follow(m); d = m.Decide(4, true, true, true);
        m.Sample(0, 4.25f, P(80), P(84), true);
        check(!m.Valid(d, 4.25f, true), "a network teleport clears movement evidence instead of proving a coordinated sprint");
        m = Team(); Follow(m, end: 2); Follow(m, 20, 21);
        check(!m.Decide(21, true, true, true).Exists, "an unsampled long gap cannot complete the following window");
        m = Team(); Follow(m); d = m.Decide(4, true, true, true);
        m.Unbind(0); m.Bind(0); Follow(m, 4.25f, 8);
        check(!m.Valid(d, 8, true), "replacing a disconnected peer cannot inherit an old candidate");
        m.Sample(0, 8.25f, P(float.NaN), P(20), true);
        check(!m.Decide(8.25f, true, true, true).Exists, "invalid transform data cannot authorize squad speech");

        m = Team(); Still(m, 0); m.Hear(0, SquadCommand.Follow, 0, 1);
        for (float t = 0.25f; t <= 4; t += 0.25f) Still(m, t);
        check(!m.Decide(4, true, true, true).Exists, "hearing FollowMe while standing or looting never produces Roger");
        m = Team(); Follow(m, end: 1); m.Hear(0, SquadCommand.Follow, 1, 1); Follow(m, 1.25f, 4);
        d = m.Decide(4, true, true, true);
        check(d.Exists && d.Reply && d.Action == SquadAction.Following,
            "heard FollowMe plus actual following produces one acknowledgement");
        m.OtherAcknowledged(4.2f);
        check(!m.Valid(d, 4.2f, true), "hearing a nearby acknowledgement cancels a pending response to the recent request");
        Follow(m, 4.25f, 40);
        check(!m.Decide(40, true, true, true).Exists, "yielding a reply does not later fill its place with a movement bark");
        m = Team(); Follow(m); m.Hear(0, SquadCommand.Follow, 4, 5);
        check(!m.Decide(4, true, true, true).Exists, "a long request is allowed to finish without an early reply or substitute");
        Follow(m, 4.25f, 9.5f);
        check(m.Decide(9.5f, true, true, true).Reply, "a five-second request can still receive a valid behaviour-based reply");
        m = Team(); Still(m, 0); m.Hear(0, SquadCommand.Follow, 0, 0);
        for (float t = 0.25f; t <= 5; t += 0.25f) Still(m, t);
        m.Hear(0, SquadCommand.Follow, 5, 0); Follow(m, 5.25f, 10);
        check(!m.Decide(10, false, true, true).Exists, "repeated same-request callbacks do not extend a stale command");

        m = Team(); Still(m, 0); m.Hit(0, 0, "enemy-a"); m.Shot(0.1f, "enemy-b"); Still(m, 0.25f); m.Shot(0.4f, "enemy-b");
        check(!m.Decide(0.5f, true, true, true).Exists, "shooting another NPC does not prove intervention against the teammate's attacker");
        m = Team(); Still(m, 0); m.Hit(0, 0, "enemy-a"); m.Shot(0.1f, "enemy-a");
        check(!m.Decide(0.2f, true, true, true).Exists, "one incidental shot alone does not establish covering");
        Still(m, 0.25f); m.Shot(0.4f, "enemy-a");
        d = m.Decide(0.5f, true, true, true);
        check(d.Exists && d.Action == SquadAction.Covering && !d.Reply,
            "a perceived teammate hit followed by local fire at the same visible NPC supports Covering");
        check(!m.Valid(d, 0.6f, false), "reloading, switching away or a malfunction cancels queued covering");
        check(!m.Valid(d, 2.5f, true), "covering expires promptly after firing stops");
        foreach (string changed in new string[] { "other-enemy", null, "down" })
        {
            m = Team(); Still(m, 0); m.Hit(0, 0, "enemy-a"); m.Shot(0.1f, "enemy-a");
            Still(m, 0.25f); m.Shot(0.4f, "enemy-a"); d = m.Decide(0.5f, true, true, true);
            if (changed == "down") m.TargetDown("enemy-a"); else m.Shot(0.6f, changed);
            check(!m.Valid(d, 0.6f, true), "changed or lost firing target / confirmed death cancels covering: " + changed);
        }
        m = Team(); Still(m, 0); m.Hit(0, 0, "enemy-a"); m.Shot(0.1f, "enemy-a"); Still(m, 0.25f); m.Shot(0.4f, "enemy-a");
        d = m.Decide(0.5f, true, true, true); m.Hit(0, 0.6f, "enemy-b");
        check(!m.Valid(d, 0.6f, true), "a different attacker replaces the old covering episode");
        m = Team(); Still(m, 0); m.Hit(0, 0, "enemy-a");
        for (float t = 0.25f; t <= 9; t += 0.25f) Still(m, t);
        m.Shot(9, "enemy-a"); m.Shot(9.4f, "enemy-a");
        check(!m.Decide(9.5f, true, true, true).Exists, "old damage cannot authorize covering indefinitely");
        m = Team(); Still(m, 0); m.Sample(0, 0.25f, P(0), P(4), false); m.Hit(0, 0.25f, "enemy-a");
        Still(m, 0.5f); m.Shot(0.5f, "enemy-a"); Still(m, 0.75f); m.Shot(0.8f, "enemy-a");
        check(!m.Decide(1, true, true, true).Exists, "unseen teammate damage is never promoted into local knowledge");
        m = Team(); Still(m, 0); m.Hear(0, SquadCommand.Cover, 0, 0);
        m.Shot(0.1f, "visible-enemy"); Still(m, 0.25f); m.Shot(0.4f, "visible-enemy");
        d = m.Decide(0.5f, true, true, true);
        check(d.Reply && d.Action == SquadAction.Covering, "CoverMe plus actual local fire supports a reply without requiring the requester to be wounded");
        m = Team(); Still(m, 0); m.Hear(0, SquadCommand.Cover, 0, 0);
        check(!m.Decide(0.5f, true, true, true).Exists, "CoverMe alone never receives an automatic promise");

        m = Team(3); Follow(m, peers: 3);
        d = m.Decide(4, true, true, true);
        check(d.Slot == 0 && d.Exists, "four-player support selects one relevant partner out of three cached peers");
        check(!m.Decide(4.1f, true, true, true).Exists, "missing audio does not immediately select another squad member");
        Follow(m, 4.25f, 40, 3);
        check(!m.Decide(40, true, true, true).Exists,
            "a stable four-person formation never cycles to weaker partners after a missing clip or elapsed cooldown");
        m = Team(3); Follow(m, peers: 3); d = m.Decide(4, true, true, true);
        m.Suspend(4.2f, manual: true); Follow(m, 4.5f, 8, 3);
        check(!m.Valid(d, 8, true) && !m.Decide(8, true, true, true).Exists,
            "manual input cancels old squad speech and keeps the decision quiet period across resume");
        m = Team(3); Follow(m, peers: 3); m.Hit(2, 4, "enemy"); m.Shot(4, "enemy");
        Follow(m, 4.25f, 4.5f, 3); m.Shot(4.4f, "enemy");
        d = m.Decide(4.5f, true, true, true);
        check(d.Slot == 2 && d.Action == SquadAction.Covering, "active assistance outranks a nearer but ordinary following relationship");
        m = Team(); Follow(m);
        check(!m.Decide(4, false, true, true).Exists, "reply-only setting does not generate unsolicited movement speech");
        m.Hear(0, SquadCommand.Follow, 4, 0); Follow(m, 4.25f, 4.5f);
        check(m.Decide(4.5f, false, true, true).Reply, "behaviour-based replies work with unsolicited coordination disabled");

        m = Team(); Follow(m); d = m.Decide(4, true, true, true);
        m.Hear(0, SquadCommand.Follow, 4, 0); Follow(m, 4.25f, 4.5f);
        var requestedFollow = m.Decide(4.5f, true, true, true);
        check(!m.Valid(d, 4.5f, true) && requestedFollow.Reply && requestedFollow.Action == SquadAction.Following,
            "new FollowMe can replace an unspoken Going without waiting out its decision cooldown");
        m.Hear(0, SquadCommand.Follow, 4.6f, 0); Follow(m, 4.75f, 5);
        check(!m.Decide(5, true, true, true).Exists,
            "duplicate FollowMe cannot turn a missing Roger into a retry or different expression");
        m = Team(); Follow(m); m.Hit(0, 4, "enemy"); m.Shot(4, "enemy");
        Follow(m, 4.25f, 4.5f); m.Shot(4.4f, "enemy");
        m.Hear(0, SquadCommand.Follow, 4.5f, 0); Follow(m, 4.75f, 5);
        d = m.Decide(5, true, true, true);
        check(d.Reply && d.Action == SquadAction.Following,
            "actual following can answer FollowMe even when the same player is also providing covering fire");

        var sample = new SquadGate(); var sight = new SquadGate(); var decision = new SquadGate();
        int samples = 0, sights = 0, decisions = 0;
        for (int i = 0; i < 10000; i++)
        {
            float now = i / 1000f;
            if (sample.Take(now, SquadMemory.SamplePeriod)) samples++;
            if (sight.Take(now, SquadMemory.SightPeriod)) sights++;
            if (decision.Take(now, SquadMemory.DecisionPeriod)) decisions++;
        }
        check(samples <= 40 && sights <= 60 && decisions <= 20, "even 1000 caller ticks per second respect 4/6/2 Hz budgets");
        check(sight.Take(100, SquadMemory.SightPeriod) && !sight.Take(100, SquadMemory.SightPeriod),
            "a long hitch cannot spend accumulated raycast credits or trigger catch-up loops");

        // Pure model cost only: no Unity, physics, network or audio in this benchmark.
        m = Team(3); Follow(m, peers: 3);
        for (int i = 0; i < 10000; i++) m.Sample(i % 3, 10 + i * 0.25f, P(i * 0.5f), P(i * 0.5f + 4), true);
        m.Combat(true);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        long watchAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
        for (int i = 0; i < 100000; i++)
        {
            float now = 3000 + i * 0.25f;
            m.Threat("enemy", P(now * 2 + 40), now);
            for (int peer = 0; peer < 3; peer++) m.Sample(peer, now, P(now * 2), P(now * 2 + 4 + peer), true);
            m.Decide(now, true, true, true);
        }
        watch.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated - watchAllocation;
        check(bytes == 0, "300000 warmed movement samples and 100000 decisions allocate zero managed bytes in the pure model");
        Console.WriteLine($"SQUAD MODEL BENCHMARK: 100000 four-player updates in {watch.Elapsed.TotalMilliseconds:F2} ms, managed allocation {bytes} bytes; excludes Unity/physics/network.");
    }
}
