using ImmersiveVoice;

internal static class SquadContinuityScenarios
{
    private static SquadPoint P(float x, float z = 0) => new(x, 0, z);
    private static SquadMemory Team(int peers = 1)
    { var m = new SquadMemory(); for (int i = 0; i < peers; i++) m.Bind(i); return m; }
    private static void Walk(SquadMemory m, bool leading = false, float start = 0, float end = 4)
    { for (float t = start; t <= end; t += 0.25f) m.Sample(0, t, P(t * 2), P(t * 2 + (leading ? -4 : 4)), true); }
    public static void Run(Action<bool, string> check)
    {
        var m = Team(); Walk(m, true);
        var d = m.Decide(4, true, true, true);
        check(d.Action == SquadAction.Leading && !d.Reply, "an established moving leader may spontaneously invite FollowMe");
        check(m.Valid(d, 4, true), "leading speech retains its actual relationship and episode");
        Walk(m, true, 4.25f, 40);
        check(!m.Decide(40, true, true, true).Exists, "a stable leader never repeats the request or fills absent audio with another partner");
        m = Team(); Walk(m, true, end: 1);
        check(!m.Decide(1, true, true, true).Exists, "passing in front of a peer briefly is not a leadership invitation");
        m = Team(); Walk(m, true); d = m.Decide(4, true, true, true);
        for (float t = 4.25f; t <= 5.5f; t += 0.25f) m.Sample(0, t, P(8), P(4), true);
        check(!m.Valid(d, 5.5f, true), "stopping invalidates an unspoken movement invitation");

        foreach (var action in new[] { SquadOwnAction.Reloading, SquadOwnAction.WeaponFault, SquadOwnAction.Healing })
        {
            m = Team(3); m.Combat(true);
            for (float t = 0; t <= 1; t += 0.25f)
            {
                for (int i = 0; i < 3; i++) m.Sample(i, t, P(0), P(4 + i), true);
                m.OwnAction(t, action, true);
            }
            d = m.Decide(1, true, true, false, survival: action == SquadOwnAction.Healing, healing: action == SquadOwnAction.Healing);
            check(d.Action == SquadAction.RequestCover && d.Slot == 0 && !d.Reply,
                "recent combat plus actual " + action + " can request cover from one nearby peer");
            check(m.Valid(d, 1, false), "requesting cover does not require one's own weapon to be ready");
            check(!m.Decide(1.1f, true, true, false).Exists, "missing CoverMe does not select a second peer");
            m.OwnAction(1.2f, SquadOwnAction.None, true);
            check(!m.Valid(d, 1.2f, false), "finishing the vulnerable action cancels the old cover request");
        }
        m = Team();
        for (float t = 0; t <= 2; t += 0.25f)
        { m.Sample(0, t, P(0), P(4), true); m.OwnAction(t, SquadOwnAction.Reloading, false); }
        check(!m.Decide(2, true, true, false).Exists, "calm magazine top-ups do not ask for cover");
        m = Team();
        for (float t = 0; t <= 2; t += 0.25f)
        { m.Sample(0, t, P(0), P(16), true); m.OwnAction(t, SquadOwnAction.Reloading, true); }
        check(!m.Decide(2, true, true, false).Exists, "a distant peer does not authorize a close cover request");
        check(SquadMemory.Allowed(SquadAction.RequestCover, new BarkContext { Healing = true, Injury = InjuryLevel.BadlyInjured })
            && !SquadMemory.Allowed(SquadAction.Leading, new BarkContext { Healing = true }), "healing allows the independently justified cover request but not ordinary coordination");
        check(!SquadMemory.Allowed(SquadAction.RequestCover, new BarkContext { RecentlyHit = true }), "fresh impact and pain still take priority over requesting cover");

        m = Team(); Walk(m);
        for (float t = 4.25f; t <= 6.5f; t += 0.25f) m.Sample(0, t, P(8), P(12), true);
        d = m.Decide(6.5f, true, true, true);
        check(d.Action == SquadAction.HoldTeam && !d.Reply, "stopping an established moving formation can spontaneously suggest holding position");
        m.Sample(0, 6.75f, P(8.5f), P(12), true);
        check(!m.Valid(d, 6.75f, true), "resuming movement invalidates the hold invitation");
        m = Team(); for (float t = 0; t <= 6; t += 0.25f) m.Sample(0, t, P(0), P(4), true);
        check(!m.Decide(6, true, true, true).Exists, "standing together without an earlier moving formation is not an automatic HoldPosition");
        m = Team(); Walk(m);
        for (float t = 4.25f; t <= 5.5f; t += 0.25f) m.Sample(0, t, P(8), P(12), true);
        m.Hear(0, SquadCommand.Hold, 5.5f, 0);
        for (float t = 5.75f; t <= 8; t += 0.25f) m.Sample(0, t, P(8), P(12), true);
        d = m.Decide(8, true, true, true);
        check(d.Reply && d.Action == SquadAction.Holding, "a teammate's Hold can acknowledge a coordinated stop that just occurred, without echoing the command");

        m = Team(); Walk(m, end: 2);
        for (float t = 2.25f; t <= 6; t += 0.25f) m.Sample(0, t, P(4), P(8 + 3 * (t - 2)), true);
        for (float t = 6.25f; t <= 8; t += 0.25f) m.Sample(0, t, P(4 + 2 * (t - 6)), P(20), true);
        d = m.Decide(8, true, true, true);
        check(d.Action == SquadAction.Rallying && !d.Reply, "an earlier moving pair separating and actively closing again can call Regroup");
        m.Sample(0, 8.25f, P(7.5f), P(20), true); m.Sample(0, 8.5f, P(7), P(20), true);
        check(!m.Valid(d, 8.5f, true), "turning away cancels a pending regroup invitation");

        m = Team(); Walk(m); d = m.Decide(4, true, true, true);
        m.Obscured(0, 4.25f, P(8.5f));
        check(m.Valid(d, 4.25f, true), "an already selected following line can bridge brief occlusion while own movement continues");
        check(!m.Decide(4.25f, true, true, true).Exists && m.Silence == SquadSilence.NoFreshObservation,
            "remembered unseen relationships cannot originate new speech");
        m.Obscured(0, 4.5f, P(9)); m.Sample(0, 5.25f, P(10.5f), P(14.5f), true);
        check(m.Valid(d, 5.25f, true), "compatible reappearance preserves the same following episode instead of restarting it");
        m = Team(); Walk(m); d = m.Decide(4, true, true, true); m.Obscured(0, 4.25f, P(8));
        check(!m.Valid(d, 4.25f, true), "stopping locally during occlusion prevents a stale movement line");
        m = Team(); Walk(m); d = m.Decide(4, true, true, true); m.Obscured(0, 4.25f, P(7.5f));
        check(!m.Valid(d, 4.25f, true), "turning back locally during occlusion invalidates remembered movement");
        m = Team(); Walk(m); d = m.Decide(4, true, true, true); m.Obscured(0, 6.25f, P(12.5f));
        check(!m.Valid(d, 6.25f, true), "occlusion longer than two seconds discards the relationship evidence");
        m = Team(); Walk(m); d = m.Decide(4, true, true, true); m.Obscured(0, 4.25f, P(80));
        check(!m.Valid(d, 4.25f, true), "local teleports cannot preserve an unseen coordination candidate");
        m = Team(); Walk(m, end: 1.5f); m.Obscured(0, 1.75f, P(3.5f)); m.Sample(0, 2.75f, P(5.5f), P(9.5f), true);
        check(!m.Decide(2.75f, true, true, true).Exists, "unseen time cannot complete a partly observed following window");

        var loss = new SquadLossMemory(); loss.Bind(0); loss.Observe(0, 0, true); loss.Observe(0, 0.5f, true);
        check(!loss.Decide(0.5f).Exists, "a corpse with no prior living teammate observation does not create a friendly death report");
        loss.Alive(0); loss.Observe(0, 1, false); loss.Observe(0, 1.5f, false);
        check(!loss.Decide(1.5f).Exists, "health=false, downed, disconnected or unspawned without a real seen corpse never proves death");
        loss.Observe(0, 2, true);
        check(!loss.Decide(2).Exists, "a single fleeting corpse sample waits for stable confirmation");
        check(!loss.Decide(2.5f).Exists, "waiting after a single glance cannot manufacture sustained corpse observation");
        loss.Observe(0, 2.5f, true); var death = loss.Decide(2.5f);
        check(death.Exists && loss.Valid(death, 3), "stable direct observation of a known teammate's corpse supports one report");
        loss.Observe(0, 50, true); loss.Observe(0, 50.5f, true);
        check(!loss.Decide(50.5f).Exists, "seeing the same body later never retries a missing death phrase");
        loss.Alive(0);
        check(!loss.Valid(death, 50.5f), "a living reappearance invalidates a queued death report");
        loss.Bind(0); loss.Alive(0); loss.Observe(0, 51, true); loss.Observe(0, 51.5f, true);
        var next = loss.Decide(51.5f);
        check(next.Exists && !loss.Valid(death, 51.5f), "reusing a roster slot cannot inherit an older teammate's death decision");
        loss.Pause();
        check(!loss.Valid(next, 52), "menus and manual cancellation discard queued loss evidence");
    }
}
