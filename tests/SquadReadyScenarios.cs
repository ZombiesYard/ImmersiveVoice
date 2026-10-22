using ImmersiveVoice;
using System.Diagnostics;

internal static class SquadReadyScenarios
{
    private static DialogueTicket Key(int caller = 1, int sequence = 1) => new() { Caller = caller, Epoch = 17, Sequence = sequence };
    private static ReadyWeaponState Gun(int ammo = 1, bool reload = false, bool fault = false, string weapon = "rifle", string mag = "old", int damage = 0, bool blocked = false, bool operating = false)
        => new() { Weapon = weapon, Magazine = mag, Ammo = ammo, Reloading = reload, Faulty = fault, Damage = damage, Blocked = blocked, Operating = operating };
    private static SquadReadyMemory Reload(bool cover = true)
    {
        var m = new SquadReadyMemory(); m.Observe(Gun(), 0); m.Observe(Gun(reload: true), 1); m.Arm(Key(), 23, 1);
        if (cover) m.Covered(Key(), 2, 2, 1, 2);
        return m;
    }
    public static void Run(Action<bool, string> check)
    {
        var m = Reload();
        check(m.Take(3) == 0, "Ready cannot close a reload still in progress");
        m.Observe(Gun(31, mag: "fresh"), 3);
        check(m.Take(3.1f) == 0, "Ready waits for cover clip end and stable action completion");
        int version = m.Take(3.5f);
        check(version > 0 && m.Valid(version, 3.5f) && m.Take(4) == 0, "one heard cover plus real reload produces exactly one closing attempt");
        check(!m.Valid(version, 8), "Ready expires five seconds after recovery without late playback");
        m.Observe(Gun(31, mag: "fresh", reload: true), 4);
        check(!m.Valid(version, 4), "another reload invalidates the queued Ready");
        m = Reload(false); m.Observe(Gun(31), 3);
        check(m.Take(4) == 0, "recovery and a possible server grant alone never authorize Ready");
        check(!m.Covered(Key(sequence: 2), 2, 2, 1, 3) && !m.Covered(Key(3), 2, 2, 1, 3), "another caller or call cannot supply the closing evidence");
        check(!m.Covered(Key(), 1, 2, 1, 3), "caller cannot provide its own cover evidence");
        check(!m.Covered(Key(), 2, 3.1f, 1, 3.2f), "cover beginning after recovery is not retroactive support");
        check(m.Covered(Key(), 2, 2, 1, 3.5f) && m.Take(3.5f) > 0, "audio before completion can bind to delayed receipt metadata");
        m = Reload(false); m.Observe(Gun(31), 3);
        check(!m.Covered(Key(), 2, 2, 1, 4.1f), "receipt delay beyond the bounded reorder window remains silent");
        m = Reload(); m.Observe(Gun(), 3);
        check(!m.Active && m.Reason == "NoUsableActionResult", "cancelled reload with unchanged ammunition cannot claim Ready");
        m = Reload(); m.Observe(Gun(1, mag: "replacement"), 3);
        check(m.Take(3.5f) > 0, "a completed equal-ammo magazine swap is real readiness");
        m = Reload(); m.Observe(Gun(0, mag: "replacement"), 3);
        check(!m.Active, "empty replacement magazine cannot yield Ready");
        m = Reload(); m.Observe(Gun(5, mag: "old"), 3);
        check(m.Take(3.5f) > 0, "internal-magazine top-up is supported without a magazine-ID change");
        foreach (var invalid in new[] { Gun(31, weapon: "other"), Gun(31, weapon: null), Gun(31, damage: 1), Gun(31, blocked: true) })
        {
            m = Reload(); m.Observe(invalid, 3);
            check(!m.Active && m.Take(4) == 0, "weapon change, treatment, survival, or new damage cancels the episode");
        }
        m = Reload(); m.Clear("Menu"); m.Observe(Gun(31), 4);
        check(m.Take(5) == 0, "menu/lifecycle pause cannot restore old cover after returning");
        m = Reload(); m.Observe(Gun(1, reload: true), 26);
        check(!m.Active, "a prolonged action cannot retain a conversation forever");
        m = new(); m.Observe(Gun(5, fault: true), 0); m.Arm(Key(), 30, 0); m.Covered(Key(), 2, 1, 1, 1); m.Observe(Gun(5), 3);
        check(m.Take(3.5f) > 0, "observed fault clear with usable ammo can close cover without a reload");
        m = new(); m.Observe(Gun(5, fault: true), 0); m.Arm(Key(), 30, 0); m.Covered(Key(), 2, 1, 1, 1);
        m.Observe(Gun(5, operating: true), 3);
        check(m.Take(3.5f) == 0 && m.Active, "cleared malfunction state cannot finish Ready while repair animation still operates");
        m.Observe(Gun(5), 4);
        check(m.Take(4.5f) > 0, "actual repair operation end releases the closing");
        m = new(); m.Observe(Gun(0, fault: true), 0); m.Arm(Key(), 30, 0); m.Covered(Key(), 2, 1, 1, 1); m.Observe(Gun(0), 3);
        check(!m.Active, "cleared fault in an empty weapon cannot claim Ready");
        m = new(); m.Observe(Gun(0), 0); check(m.Arm(Key(), 20, 0), "actual empty-weapon call can wait for its following reload");
        m.Covered(Key(), 2, 1, 1, 1); m.Observe(Gun(0, reload: true), 2); m.Observe(Gun(30), 3);
        check(m.Take(3.5f) > 0, "empty-call cover survives into the same weapon's completed reload");
        m = new(); m.Observe(Gun(30), 0);
        check(!m.Arm(Key(), 30, 0) && !m.Arm(Key(), 20, 0), "ordinary cover and nonempty gun cannot manufacture a recovery");
        check(SquadDialogue.Command(63) == SquadCommand.None, "Ready does not open another reply conversation");

        var arbiter = new DialogueArbiter();
        for (int i = 1; i <= 4; i++) { arbiter.Open(Key(i), 23, 0, 1, 0); arbiter.Claim(Key(i), i % 4 + 1, 1); }
        check(!arbiter.MatchReply(1, 2, 60, 2, 2).Exists, "host grant without actual playback has no reply receipt");
        check(!arbiter.Reply(Key(), 3, 60, 60, 1, 2) && !arbiter.Reply(Key(), 2, 67, 60, 1, 2), "wrong actor and wrong phrase are not cover receipts");
        for (int i = 1; i <= 4; i++)
        {
            check(arbiter.Reply(Key(i), i % 4 + 1, 60, 60 + i, 1, 2), "four overlapping calls each accept their own winner's playback");
            check(arbiter.MatchReply(i, i % 4 + 1, 60 + i, 2, 2).Same(Key(i)), "cover receipt stays tied to its caller, winner and clip");
            check(!arbiter.MatchReply(i, (i + 1) % 4 + 1, 60 + i, 2, 2).Exists, "other teammate's identical sound cannot close this call");
        }
        check(!arbiter.Reply(Key(), 2, 60, 61, 1, 3) && !arbiter.MatchReply(1, 2, 61, 2, 5).Exists, "duplicate receipts cannot extend their bounded matching window");
        arbiter.Open(Key(sequence: 2), 23, 0, 1, 3);
        check(!arbiter.MatchReply(1, 2, 61, 2, 3).Exists && !arbiter.Reply(Key(), 2, 60, 61, 1, 3), "new request discards the old reply receipt");
        arbiter.Clear(); check(!arbiter.MatchReply(2, 3, 62, 2, 2).Exists, "disconnect/teardown clears reply receipts");
        arbiter.Open(Key(), 31, 0, 1, 0); arbiter.Claim(Key(), 2, 1);
        check(!arbiter.Reply(Key(), 2, 60, 60, 1, 2), "a follow request cannot become a cover-and-recovery conversation");

        var policy = new VoicePolicy(); var ready = new VoiceCue { Phrase = 63, Intent = VoiceIntent.TeamReady, Key = "ready", Group = "team-ready", Repeat = 30, GroupGap = 30 };
        policy.Spoken(0, 1, 8, true, false, 0, new VoiceCue { Key = "reload", Group = "reload" });
        check(policy.BlockReason(ready, .5f) == "SpeechPlaying" && policy.CanUse(ready, 3), "linked Ready respects speech but need not wait the ambient global gap");
        policy.Spoken(3, 1, 8, true, false, 0, ready);
        check(policy.BlockReason(ready, 6) == "RecentSubject", "Ready still has a thirty-second repeat limit");
        check(PlaybackRules.EventVoice(ready) && PlaybackRules.Movement(VoiceIntent.TeamReady, false, true, false, false) == null
            && PlaybackRules.Movement(VoiceIntent.TeamReady, false, false, false, true) == "Stealth", "Ready can close while aiming, yields long breath, and still respects stealth");
        policy.Manual(7, 10); check(policy.BlockReason(ready, 8) == "ManualPriority", "manual speech takes priority over Ready");

        var four = new[] { Reload(), Reload(), Reload(), Reload() }; var busy = Gun(reload: true);
        foreach (var state in four) state.Observe(busy, 2);
        long alloc = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 100000; i++) foreach (var state in four) { state.Observe(busy, 2); state.Take(2); }
        long elapsed = Stopwatch.GetTimestamp() - start;
        check(GC.GetAllocatedBytesForCurrentThread() == alloc, "four Ready memories allocate zero managed bytes across 100,000 active updates");
        Console.WriteLine($"Ready benchmark: 100,000 four-player updates in {elapsed * 1000d / Stopwatch.Frequency:F2} ms (model only).");
    }
}
