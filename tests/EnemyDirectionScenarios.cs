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

        StealthAndConversation(check);
    }

    private static void Step(StealthApproachMemory memory, float now, float selfX, float otherX,
        bool stealth, int peer = 0, bool perceived = true, bool sameLayer = true, bool combat = false,
        string threat = "enemy", bool alive = true)
    {
        memory.Threat(threat, P(40, 0), alive, true, now);
        memory.Sample(peer, now, P(selfX, 0), P(otherX, 1 + peer), perceived, sameLayer,
            stealth, stealth, combat);
    }

    private static StealthApproachDecision CompleteApproach(StealthApproachMemory memory, int peer = 0)
    {
        for (int i = 0; i <= 3; i++)
        {
            float now = i * .25f;
            Step(memory, now, now * 2f, now * 2f + 1f, false, peer);
        }
        for (int i = 1; i <= 7; i++)
        {
            float now = .75f + i * .25f;
            float position = 1.5f + i * .1f;
            Step(memory, now, position, position + 1f, true, peer);
        }
        return memory.Select(2.5f);
    }

    private static void StealthAndConversation(Action<bool, string> check)
    {
        check(TacticalStageRules.Phrase(TacticalStageKind.EnemyConversation) == 17
            && TacticalStageRules.Phrase(TacticalStageKind.Silence) == 39,
            "supported stage evidence maps only to OnEnemyConversation and Silence");
        check(!TacticalStageRules.SupportedPhrase(35) && !TacticalStageRules.SupportedPhrase(58)
            && !TacticalStageRules.SupportedPhrase(77),
            "Look, Clear, and alternate LostVisual remain unsupported without exact perceptible evidence");

        var stealth = new StealthApproachMemory();
        var approach = CompleteApproach(stealth);
        check(approach.Exists && stealth.Valid(approach, 2.5f),
            "joint normal movement followed by sustained quiet approach to one live threat supports Silence");
        check(!stealth.Select(2.6f).Exists,
            "one shared approach chooses Silence only once even with four-player sampling");

        stealth = new StealthApproachMemory();
        for (int i = 0; i <= 10; i++) Step(stealth, i * .25f, i * .1f, i * .1f + 1f, true);
        check(!stealth.Select(2.5f).Exists,
            "starting already in stealth cannot manufacture the required group transition");
        stealth = new StealthApproachMemory();
        for (int i = 0; i <= 3; i++) Step(stealth, i * .25f, i * .5f, i * .5f + 1f, false);
        for (int i = 1; i <= 7; i++)
        {
            float now = .75f + i * .25f, x = 1.5f + i * .1f;
            stealth.Threat("enemy", P(40, 0), true, true, now);
            stealth.Sample(0, now, P(x, 0), P(x + 1f, 1), true, true, true, false, false);
        }
        check(!stealth.Select(2.5f).Exists,
            "only the local player's slow or crouched movement cannot command a normally moving teammate");
        stealth = new StealthApproachMemory();
        for (int i = 0; i <= 3; i++) Step(stealth, i * .25f, i * .5f, i * .5f + 1f, false);
        for (int i = 1; i <= 7; i++)
        {
            float now = .75f + i * .25f, x = 1.5f + i * .1f;
            Step(stealth, now, x, x + 1f, true, perceived: false);
        }
        check(!stealth.Select(2.5f).Exists,
            "an unperceived teammate cannot turn the local stealth key or stance into a voice command");

        stealth = new StealthApproachMemory();
        for (int i = 0; i <= 3; i++) Step(stealth, i * .25f, i * .5f, i * .5f + 1f, false);
        for (int i = 1; i <= 7; i++)
        {
            float now = .75f + i * .25f, x = 1.5f + i * .1f;
            Step(stealth, now, x, x + 1f, true, sameLayer: i != 3);
        }
        check(!stealth.Select(2.5f).Exists,
            "losing the teammate layer during the transition discards accumulated quiet movement");

        stealth = new StealthApproachMemory();
        approach = CompleteApproach(stealth);
        stealth.Threat("enemy", P(40, 0), false, true, 2.6f);
        check(!stealth.Valid(approach, 2.6f), "a downed or lost threat cancels queued Silence");
        stealth = new StealthApproachMemory();
        approach = CompleteApproach(stealth);
        stealth.Lost("enemy");
        check(!stealth.Valid(approach, 2.6f), "an explicit visual-loss transition also cancels queued Silence");
        stealth = new StealthApproachMemory();
        approach = CompleteApproach(stealth);
        stealth.Threat("other", P(35, 0), true, true, 2.6f);
        check(!stealth.Valid(approach, 2.6f) && !stealth.Select(2.6f).Exists,
            "transferring focus to another threat requires a new observed movement transition");
        stealth = new StealthApproachMemory();
        approach = CompleteApproach(stealth);
        Step(stealth, 2.6f, 2.3f, 3.3f, true, combat: true);
        check(!stealth.Valid(approach, 2.6f) && !stealth.Select(2.6f).Exists,
            "actual combat cancels stealth-stage speech immediately");

        stealth = new StealthApproachMemory();
        for (int i = 0; i <= 3; i++)
        {
            float now = i * .25f;
            for (int peer = 0; peer < StealthApproachMemory.PeerCapacity; peer++)
                Step(stealth, now, now * 2f, now * 2f + 1f, false, peer);
        }
        for (int i = 1; i <= 7; i++)
        {
            float now = .75f + i * .25f, x = 1.5f + i * .1f;
            for (int peer = 0; peer < StealthApproachMemory.PeerCapacity; peer++)
                Step(stealth, now, x, x + 1f, true, peer);
        }
        check(stealth.Select(2.5f).Exists && !stealth.Select(2.5f).Exists,
            "three eligible teammates still produce one local Silence decision");

        var conversation = new EnemyConversationMemory();
        check(!conversation.Hear("npc", 1, 0, false, true, true, true).Exists
            && !conversation.Hear("npc", 1, 0, true, false, true, true).Exists
            && !conversation.Hear("npc", 1, 0, true, true, false, true).Exists
            && !conversation.Hear("npc", 1, 0, true, true, true, false).Exists,
            "hidden AI state, inaudible audio, non-hostility, and physiological noise cannot assert enemy conversation");
        var heard = conversation.Hear("npc", 1, 1, true, true, true, true);
        check(heard.Exists && conversation.Valid(heard, 1.1f),
            "a real audible hostile NPC voice supports exact OnEnemyConversation");
        check(!conversation.Hear("npc", 1, 2, true, true, true, true).Exists,
            "the same NPC is reported once in a raid despite repeated native callbacks");
        check(conversation.Hear("other", 1, 2, true, true, true, true).Exists,
            "a distinct actually heard hostile NPC retains its own bounded observation");
        check(conversation.Hear("third", 1, 2.1f, true, true, true, true).Exists
            && conversation.Hear("fourth", 1, 2.2f, true, true, true, true).Exists
            && !conversation.Hear("overflow", 1, 2.3f, true, true, true, true).Exists
            && !conversation.Hear("npc", 1, 2.4f, true, true, true, true).Exists,
            "four-NPC storage fails closed without evicting and repeating an earlier raid identity");
        check(!conversation.Valid(heard, 6.1f), "enemy conversation expires instead of replaying after the exchange");
        check(conversation.Hear("npc", 2, 10, true, true, true, true).Exists,
            "a new raid round resets per-NPC conversation deduplication");
        conversation.Clear();
        check(!conversation.Valid(heard, 1.1f), "lifecycle clear discards queued enemy conversation");

        var four = new[] { new StealthApproachMemory(), new StealthApproachMemory(),
            new StealthApproachMemory(), new StealthApproachMemory() };
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 100000; i++)
        {
            float now = i * .25f, x = i % 100;
            for (int client = 0; client < four.Length; client++)
            {
                four[client].Threat("enemy", P(1000, 0), true, true, now);
                for (int peer = 0; peer < StealthApproachMemory.PeerCapacity; peer++)
                    four[client].Sample(peer, now, P(x, 0), P(x + 1, peer + 1), true, true, true, true, false);
            }
        }
        long elapsed = Stopwatch.GetTimestamp() - start;
        check(GC.GetAllocatedBytesForCurrentThread() == allocated,
            "four clients with three peers allocate zero managed bytes across 100,000 stage updates");
        Console.WriteLine($"Stage benchmark: 100,000 four-client/three-peer updates in {elapsed * 1000d / Stopwatch.Frequency:F2} ms (model only).");
    }
}
