using ImmersiveVoice;

internal static class SquadTacticalScenarios
{
    private static SquadPoint P(float x, float z = 0, float y = 0) => new(x, y, z);
    private static SquadMemory Team(bool combat = true, bool threat = true, int peers = 1)
    {
        var m = new SquadMemory();
        for (int i = 0; i < peers; i++) m.Bind(i);
        m.Combat(combat);
        if (threat) m.Threat("enemy", P(40), 0);
        return m;
    }
    private static void Move(SquadMemory m, bool retreat = false, float start = 0, float end = 4, int peers = 1)
    {
        for (float t = start; t <= end; t += 0.25f)
            for (int i = 0; i < peers; i++)
                m.Sample(i, t, P(t * (retreat ? -2 : 2)), P(t * (retreat ? -2 : 2), 4 + i * 2), true);
    }
    public static void Run(Action<bool, string> check)
    {
        var m = Team(); Move(m);
        var d = m.Decide(4, true, true, true);
        check(d.Action == SquadAction.Advancing && !d.Reply && m.Valid(d, 4, true),
            "parallel teammates actually approaching a recent visible combat anchor can express advance");
        m = Team(); Move(m, true); d = m.Decide(4, true, true, true);
        check(d.Action == SquadAction.Retreating && !d.Reply && m.Valid(d, 4, true),
            "sustained joint movement away from the known fight supports retreat");
        m = Team(); Move(m, end: 2);
        check(!m.Decide(2, true, true, true).Exists, "a brief approach does not establish joint advance");
        foreach (string scenario in new[] { "no-threat", "no-combat", "stationary-peer", "stationary-self",
            "opposite", "lateral", "floor", "enemy-motion-only", "slow-drift" })
        {
            m = Team(combat: scenario != "no-combat", threat: scenario != "no-threat");
            for (float t = 0; t <= 4; t += 0.25f)
            {
                var self = P(t * 2); var other = P(t * 2, 4);
                if (scenario == "stationary-peer") other = P(0, 4);
                if (scenario == "stationary-self") self = P(0);
                if (scenario == "opposite") other = P(-t * 2, 4);
                if (scenario == "lateral") { self = P(0, t * 2); other = P(4, t * 2); }
                if (scenario == "floor") other = P(t * 2, 4, 4);
                if (scenario == "enemy-motion-only") { self = P(0); other = P(0, 4); m.Threat("enemy", P(40 - t * 2), t); }
                if (scenario == "slow-drift") { self = P(t * 0.2f); other = P(t * 0.2f, 4); }
                m.Sample(0, t, self, other, true);
            }
            check(!m.Decide(4, true, true, true).Exists, scenario + " cannot manufacture tactical movement");
        }
        foreach (string scenario in new[] { "target-down", "target-changed", "target-jump", "calm", "stale", "peer-lost", "teleport", "pause" })
        {
            m = Team(); Move(m); d = m.Decide(4, true, true, true);
            float now = 4.25f;
            switch (scenario)
            {
                case "target-down": m.TargetDown("enemy"); break;
                case "target-changed": m.Threat("new-enemy", P(40), now); break;
                case "target-jump": m.Threat("enemy", P(-40), now); break;
                case "calm": m.Combat(false); break;
                case "stale": Move(m, start: 4.25f, end: 8.25f); now = 8.25f; break;
                case "peer-lost": m.Sample(0, now, P(8.5f), P(8.5f, 4), false); break;
                case "teleport": m.Sample(0, now, P(80), P(80, 4), true); break;
                default: m.Suspend(now); break;
            }
            check(!m.Valid(d, now, true), scenario + " cancels an already queued advance");
        }
        m = Team(); Move(m); d = m.Decide(4, true, true, true);
        for (float t = 4.25f; t <= 8; t += 0.25f)
            m.Sample(0, t, P(8 - (t - 4) * 2), P(8 - (t - 4) * 2, 4), true);
        var retreat = m.Decide(8, true, true, true);
        check(!m.Valid(d, 8, true) && retreat.Action == SquadAction.Retreating,
            "turning away from the fight cancels advance and can select retreat without waiting out advance's 25 seconds");
        check(!m.Decide(8.1f, true, true, true).Exists, "retreat transition never retries absent audio");
        m = Team(); Move(m, true);
        check(m.Decide(4, true, true, true, survival: true).Action == SquadAction.Retreating,
            "existing serious wounds do not themselves forbid an actual retreat expression");
        m = Team(); Move(m);
        check(!m.Decide(4, true, true, true, survival: true).Exists, "serious survival concerns still suppress advance");
        m = Team();
        for (float t = 0; t <= 4; t += 0.25f) m.Sample(0, t, P(-t * 2), P(-t * 2 - 4), true);
        m.Hear(0, SquadCommand.Follow, 4, 0);
        for (float t = 4.25f; t <= 4.5f; t += 0.25f) m.Sample(0, t, P(-t * 2), P(-t * 2 - 4), true);
        check(m.Decide(4.5f, true, true, true, survival: true).Action == SquadAction.Retreating,
            "an unsuitable following acknowledgement cannot hide a wounded player's real retreat");

        foreach (bool back in new[] { false, true })
        {
            m = Team(); Move(m, back, end: 1);
            m.Hear(0, back ? SquadCommand.Retreat : SquadCommand.Advance, 1, 1);
            Move(m, back, 1.25f); d = m.Decide(4, false, true, true);
            check(d.Reply && d.Action == (back ? SquadAction.Retreating : SquadAction.Advancing),
                "a matching " + (back ? "retreat" : "advance") + " request receives a behavior-based reply");
            m.OtherAcknowledged(4.1f);
            check(!m.Valid(d, 4.1f, true), "another acknowledgement cancels the tactical reply");
        }
        m = Team(); Move(m, true, end: 1); m.Hear(0, SquadCommand.Advance, 1, 0); Move(m, true, 1.25f);
        check(!m.Decide(4, false, true, true).Exists, "retreat does not acknowledge an advance request");
        m = Team(); Move(m); d = m.Decide(4, true, true, true);
        m.Hear(0, SquadCommand.Advance, 4, 0); Move(m, start: 4.25f, end: 4.5f);
        var reply = m.Decide(4.5f, true, true, true);
        check(!m.Valid(d, 4.5f, true) && reply.Reply, "a new real matching request replaces an unspoken automatic statement");
        check(!m.Decide(4.6f, true, true, true).Exists, "a selected or missing acknowledgement cannot retry as another phrase");
        m.Suspend(4.6f, manual: true); Move(m, start: 4.75f, end: 8);
        m.Hear(0, SquadCommand.Advance, 8, 0); m.Threat("enemy", P(40), 8);
        Move(m, start: 8.25f, end: 12);
        check(!m.Decide(12, true, true, true).Exists, "new requests never bypass the manual quiet period");

        m = Team(); Move(m); d = m.Decide(4, true, false, true);
        m.Hear(0, SquadCommand.Advance, 4, 0); Move(m, start: 4.25f, end: 4.5f);
        check(m.Valid(d, 4.5f, true, replies: false) && !m.Decide(4.5f, true, false, true).Exists,
            "with replies disabled, a heard request does not cancel independent movement speech or generate Roger");

        m = Team(false, false);
        for (float t = 0; t <= 1; t += 0.25f) m.Sample(0, t, P(t * 2), P(10), true);
        m.Hear(0, SquadCommand.Regroup, 1, 0);
        for (float t = 1.25f; t <= 4; t += 0.25f) m.Sample(0, t, P(t * 2), P(10), true);
        d = m.Decide(4, false, true, true);
        check(d.Reply && d.Action == SquadAction.Regrouping && m.Valid(d, 4, true),
            "hearing Regroup and actually closing at least two meters into six meters supports Roger");
        for (float t = 4.25f; t <= 4.5f; t += 0.25f) m.Sample(0, t, P(8), P(10), true);
        check(m.Valid(d, 4.5f, true), "stopping upon arrival briefly preserves the regroup reply");
        for (float t = 4.75f; t <= 5.5f; t += 0.25f) m.Sample(0, t, P(8 - (t - 4.5f) * 2), P(10), true);
        check(!m.Valid(d, 5.5f, true), "moving away again cancels a regroup reply");
        m = Team(false, false); m.Sample(0, 0, P(0), P(10), true); m.Hear(0, SquadCommand.Regroup, 0, 0);
        for (float t = 0.25f; t <= 4; t += 0.25f) m.Sample(0, t, P(0), P(10 - t * 2), true);
        check(!m.Decide(4, true, true, true).Exists, "a caller approaching an idle local player is not local compliance with Regroup");
        m = Team(false, false);
        for (float t = 0; t <= 4; t += 0.25f) m.Sample(0, t, P(t * 2), P(10), true);
        check(!m.Decide(4, true, true, true).Exists, "mere approach without a request does not fabricate a regroup acknowledgement");

        m = Team(false, false);
        for (float t = 0; t <= 1; t += 0.25f) m.Sample(0, t, P(t * 2), P(t * 2 + 4), true);
        m.Hear(0, SquadCommand.Hold, 1, 0);
        for (float t = 1.25f; t <= 4; t += 0.25f) m.Sample(0, t, P(2), P(6), true);
        d = m.Decide(4, false, true, true);
        check(d.Reply && d.Action == SquadAction.Holding && m.Valid(d, 4, true),
            "hearing Hold while moving and then stopping nearby for 1.5 seconds supports Roger");
        m.Sample(0, 4.25f, P(2.5f), P(6), true);
        check(!m.Valid(d, 4.25f, true), "resuming movement cancels the queued hold reply");
        m = Team(false, false); m.Sample(0, 0, P(0), P(4), true); m.Hear(0, SquadCommand.Hold, 0, 0);
        for (float t = 0.25f; t <= 4; t += 0.25f) m.Sample(0, t, P(0), P(4), true);
        check(!m.Decide(4, true, true, true).Exists, "already stationary players do not answer Hold merely for standing still");

        m = Team(peers: 3); Move(m, peers: 3); d = m.Decide(4, true, true, true);
        check(d.Exists && d.Slot == 0, "four-player advance selects one nearby relationship");
        for (float t = 4.25f; t <= 40; t += 0.25f)
        {
            m.Threat("enemy", P((t - 4) * 2 + 40), t);
            for (int i = 0; i < 3; i++) m.Sample(i, t, P(t * 2), P(t * 2, 4 + i * 2), true);
        }
        check(!m.Decide(40, true, true, true).Exists, "a persistent advance never rotates to weaker peers after cooldown or missing audio");
    }
}
