using ImmersiveVoice;
using System.Diagnostics;

internal static class SquadDialogueScenarios
{
    private static SquadPoint P(float x) => new(x, 0, 0);
    private static SquadMemory Team() { var m = new SquadMemory(); m.Bind(0); return m; }
    public static void Run(Action<bool, string> check)
    {
        foreach (var pair in new[] { (SquadAction.Following, 59), (SquadAction.Advancing, 59),
            (SquadAction.Retreating, 59), (SquadAction.Covering, 60), (SquadAction.Holding, 64),
            (SquadAction.Regrouping, 64), (SquadAction.Compliment, 66) })
        {
            var d = new SquadDecision { Action = pair.Item1, Reply = true };
            check(SquadDialogue.Phrase(d) == pair.Item2 && SquadDialogue.Phrase(d) != 67,
                "action-specific reply: " + pair.Item1);
            check(SquadDialogue.Command(SquadDialogue.Phrase(d)) == SquadCommand.None,
                "reply never initiates a reply loop: " + pair.Item1);
        }
        check(SquadDialogue.Command(23) == SquadCommand.Cover && SquadDialogue.Command(20) == SquadCommand.Cover,
            "actual reload and empty-weapon calls create support opportunities");
        check(SquadDialogue.Command(75) == SquadCommand.Praise && SquadDialogue.Command(18) == SquadCommand.None,
            "witnessed kill report can be acknowledged without treating native combat hints as proof");
        check(!ReactionQueue.ExchangeInvalidates(ReactionKind.EnemyDown, VoiceIntent.TacticalEnemyDown, true),
            "fresh witnessed team kill report survives renewed combat");
        check(ReactionQueue.ExchangeInvalidates(ReactionKind.EnemyDown, VoiceIntent.KillTaunt, true)
            && ReactionQueue.ExchangeInvalidates(ReactionKind.Recovery, VoiceIntent.Recovery, true)
            && ReactionQueue.ExchangeInvalidates(ReactionKind.AfterTreatment, VoiceIntent.Injury, true),
            "renewed combat still invalidates emotional and recovery endings");
        var replies = Team(); replies.Sample(0, 0, P(0), P(3), true);
        replies.Hear(0, SquadCommand.Praise, 0, 1);
        for (float t = .25f; t <= 2; t += .25f) replies.Sample(0, t, P(0), P(3), true);
        check(replies.Decide(2, false, true, true).Action == SquadAction.Compliment, "heard enemy-down report supports GoodWork");
        replies.Sample(0, 11, P(0), P(3), true); replies.Hear(0, SquadCommand.Praise, 11, 1);
        for (float t = 11.25f; t <= 13; t += .25f) replies.Sample(0, t, P(0), P(3), true);
        check(replies.Decide(13, false, true, true).Reply, "new heard report is not starved by the old 25-second travel cooldown");

        var m = Team(); m.Combat(true); m.Threat("enemy", P(50), 0);
        for (float t = 0; t <= 4; t += .25f) m.Sample(0, t, P(t * 2), P(0), true);
        check(m.Decide(4, true, true, true).Action == SquadAction.Advancing, "local advance works while teammate stays on overwatch");
        m = Team(); m.Combat(true); m.Threat("enemy", P(50), 0);
        for (float t = 0; t <= 4; t += .25f) m.Sample(0, t, P(-t * 2), P(0), true);
        check(m.Decide(4, true, true, true).Action == SquadAction.Retreating, "retreat does not require teammate to move first");

        m = Team(); m.Combat(true); m.Threat("enemy", P(50), 0);
        for (float t = 0; t <= 4; t += .25f) m.Sample(0, t, P(0), P(t * 2 + 3), true);
        m.Shot(3.5f, "enemy"); m.Shot(3.8f, "enemy");
        check(m.Decide(4, true, true, true).Action == SquadAction.Covering,
            "real fire at a known threat covers a moving teammate without requiring injury");
        m.Suspend(4.1f); m.Threat("new-enemy", P(50), 4.25f);
        m.Sample(0, 4.25f, P(0), P(11), true);
        m.Shot(4.3f, "new-enemy"); m.Shot(4.6f, "new-enemy");
        check(!m.Evidence(0, 4.6f).Contains("CoverReason=Ready"),
            "pause discards stale teammate movement before fresh support evidence can accumulate");
        m = Team(); m.Combat(true); m.Threat("enemy", P(50), 0);
        for (float t = 0; t <= 4; t += .25f) m.Sample(0, t, P(0), P(t + 3), true);
        m.Shot(3.5f, "enemy"); m.Shot(3.8f, "enemy"); m.Decide(4, true, true, true);
        m.Threat("next-enemy", P(50), 8);
        for (float t = 4.25f; t <= 12; t += .25f)
        {
            m.Sample(0, t, P(0), P(t + 3), true);
            if (t == 11.5f || t == 11.75f) m.Shot(t, "next-enemy");
        }
        check(m.Decide(12, true, true, true).Action == SquadAction.Covering,
            "new related target permits another cover statement after tactical cooldown");
        check(!m.Decide(12.1f, true, true, true).Exists, "same cover episode is not repeated after changing targets");
        m = Team(); m.Combat(true); m.Threat("enemy", P(50), 0);
        for (float t = 0; t <= 4; t += .25f) m.Sample(0, t, P(0), P(t * 2 + 3), true);
        m.Shot(3.5f, "unrelated"); m.Shot(3.8f, "unrelated");
        check(m.Decide(4, true, true, true).Action != SquadAction.Covering, "unrelated fire is not manufactured into covering");
        m = Team();
        for (float t = 0; t <= 4; t += .25f) m.Sample(0, t, P(t * 2), P(t * 2 - 4), true);
        check(m.Decide(4, true, true, true).Action == SquadAction.Leading, "initial travel can establish a lead");
        m.Suspend(5);
        for (float t = 35; t <= 40; t += .25f) m.Sample(0, t, P(t * 2), P(t * 2 - 4), true);
        check(!m.Decide(40, true, true, true).Exists, "menu re-observation does not immediately repeat FollowMe after shared cooldown");
        m.Combat(true); m.Threat("enemy", P(150), 40);
        for (float t = 40.25f; t <= 44; t += .25f) m.Sample(0, t, P(t * 2), P(t * 2 - 4), true);
        check(m.Decide(44, true, true, true).Action == SquadAction.Advancing, "travel deduplication does not block new combat movement");

        // Every ordering of three responders in a four-human squad; absent audio
        // excludes a candidate before it submits a claim. Duplicate, late and
        // cancelled-winner messages must never produce a second winner.
        int scenarios = 0;
        foreach (int first in new[] { 2, 3, 4 })
        foreach (int second in new[] { 2, 3, 4 })
        foreach (int third in new[] { 2, 3, 4 })
        {
            if (first == second || second == third || first == third) continue;
            for (int audioMask = 0; audioMask < 8; audioMask++)
            {
                var a = new DialogueArbiter(); var ticket = new DialogueTicket { Caller = 1, Epoch = 10, Sequence = 1 };
                object owner = new();
                if (!a.Open(ticket, 31, 3, 1, 10, owner)) throw new Exception("open failed");
                int wins = 0, expected = 0;
                foreach (int id in new[] { first, second, third })
                {
                    if ((audioMask & (1 << (id - 2))) == 0) continue;
                    if (expected == 0) expected = id;
                    if (a.Claim(ticket, id, 12)) wins++;
                    if (a.Claim(ticket, id, 12.2f)) throw new Exception("duplicate grant");
                }
                if (wins != (audioMask == 0 ? 0 : 1) || a.Winner(ticket, 13) != expected) throw new Exception("four-player arbitration failed");
                if (a.Open(ticket, 31, 3, 1, 30, owner) || a.Claim(ticket, 2, 30)) throw new Exception("late replay reopened expired call");
                if (a.Winner(ticket, 30) != -1) throw new Exception("expired grant");
                scenarios++;
            }
        }
        check(scenarios == 48, "48 four-player arrival/audio scenarios: at most one grant, no fallback or late replay");
        var arbiter = new DialogueArbiter(); object connection = new();
        var key = new DialogueTicket { Caller = 1, Epoch = 20, Sequence = 1 };
        check(arbiter.Open(key, 23, 7, 2, 0, connection), "arbitration opens with a bounded lifetime");
        check(!arbiter.Match(1, 23, 8, 0, 1).Exists && !arbiter.Match(1, 23, 7, 3, 3).Exists,
            "different audio or metadata more than two seconds from heard sound cannot authorize a reply");
        check(arbiter.Match(1, 23, 7, .3f, 1).Same(key), "matching audio and metadata form a call ticket");
        check(!arbiter.Claim(key, 1, 1), "caller cannot answer itself");
        check(!arbiter.Open(new DialogueTicket { Caller = 1, Epoch = 21, Sequence = 2 }, 23, 7, 2, 1, connection),
            "a stale or foreign epoch cannot replace a live connection");
        key.Sequence++;
        check(arbiter.Open(key, 23, 7, 2, 1, connection), "new actual call supersedes previous ticket");
        arbiter.RemoveOwner(connection);
        check(arbiter.Winner(key, 2) == -1, "disconnect revokes outstanding call grants");
        for (int i = 1; i <= 4; i++) arbiter.Open(new DialogueTicket { Caller = i, Epoch = 30, Sequence = 1 }, 31, 0, 1, 5);
        check(!arbiter.Open(new DialogueTicket { Caller = 5, Epoch = 30, Sequence = 1 }, 31, 0, 1, 5), "host arbitration retains at most four human callers");
        arbiter.Clear(); check(arbiter.Winner(key, 6) == -1, "raid shutdown clears all dialogue state");
        object[] identities = { new(), new(), new(), new(), new() };
        for (int i = 1; i <= 4; i++) arbiter.Open(new DialogueTicket { Caller = i, Epoch = 30, Sequence = 1 }, 31, 0, 1, 5, identity: identities[i - 1]);
        identities[0] = new object(); identities[1] = null;
        arbiter.Reconcile(id => identities[id - 1]);
        check(arbiter.Open(new DialogueTicket { Caller = 1, Epoch = 31, Sequence = 1 }, 31, 0, 1, 6, identity: identities[0]),
            "reconnected actor with reused NetId starts a fresh epoch without inheriting stale grants");
        check(arbiter.Open(new DialogueTicket { Caller = 5, Epoch = 32, Sequence = 1 }, 31, 0, 1, 6, identity: identities[4]),
            "departed caller releases its bounded slot for a late join");
        arbiter.Clear();

        // A million constant-capacity reads; allocation is a real hot-path constraint,
        // timing is reported rather than asserted against a machine-specific threshold.
        key = new DialogueTicket { Caller = 1, Epoch = 1, Sequence = 1 };
        arbiter.Open(key, 31, 0, 1, 0); arbiter.Winner(key, 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        int total = 0;
        for (int i = 0; i < 1000000; i++) total += arbiter.Winner(key, 1);
        long elapsed = Stopwatch.GetTimestamp() - started;
        check(GC.GetAllocatedBytesForCurrentThread() == allocated && total == 0, "dialogue lookup hot path allocates zero managed bytes");
        Console.WriteLine($"Dialogue benchmark: 1,000,000 bounded lookups in {elapsed * 1000d / Stopwatch.Frequency:F2} ms (model only).");
    }
}
