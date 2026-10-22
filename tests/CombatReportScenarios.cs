using ImmersiveVoice;

internal static class CombatReportScenarios
{
    public static void Run(Action<bool, string> check)
    {
        var memory = new CombatReportMemory();
        check(!memory.ObserveHit("hidden", 1, true, false, true, true, false, false).Exists,
            "an unseen impact cannot reveal a target through geometry");
        check(!memory.ObserveHit("remote", 1, true, true, false, true, false, false).Exists,
            "a teammate's replicated hit cannot become the local player's EnemyHit line");
        check(!memory.ObserveHit("armor", 1, true, true, true, false, false, false).Exists,
            "a zero-damage hit hint cannot assert that the enemy was hit");

        CombatReport hit = memory.ObserveHit("pmc", 10, true, true, true, true, false, false);
        check(hit.Kind == CombatReportKind.EnemyHit && memory.Valid(hit, 11),
            "a locally dealt, locally perceived real impact supports EnemyHit");
        check(!memory.ObserveHit("pmc", 10.1f, true, true, true, true, false, false).Exists,
            "pellets and automatic-fire callbacks do not multiply hit reports");
        check(!memory.Valid(hit, 12.5f), "a delayed hit report expires with its living observation");

        CombatReport scav = memory.ObserveLiving("scav", 20, true, true, true);
        check(scav.Kind == CombatReportKind.ScavSpotted,
            "an actually perceived hostile ordinary Scav selects the precise Scav report");
        check(!memory.ObserveLiving("scav", 20.2f, true, true, true).Exists,
            "the same visible Scav is identified once");
        memory.ObserveLiving("unstable-scav", 20.3f, true, true, true, reportable: false);
        check(!memory.ObserveLiving("unstable-scav", 20.5f, true, true, true, reportable: false).Exists,
            "visual samples can retain living evidence without announcing an unstable contact");
        CombatReport scavDown = memory.ObserveDeath("scav", 21, true, true, true, true);
        check(scavDown.Kind == CombatReportKind.ScavDown && !memory.Valid(scav, 21) && memory.Valid(scavDown, 21),
            "visible Scav death upgrades and invalidates a pending contact report");
        check(!memory.ObserveDeath("scav", 21.1f, true, true, true, true).Exists,
            "one death cannot create multiple closing reports");

        memory.ObserveLiving("unknown", 30, true, true, false);
        CombatReport genericDown = memory.ObserveDeath("unknown", 31, true, true, true, false);
        check(genericDown.Kind == CombatReportKind.EnemyDown,
            "an uncertain hostile retains the factual generic down meaning");
        memory.ObserveLiving("background-death", 40, true, true, false);
        check(!memory.ObserveDeath("background-death", 41, true, false, true, false).Exists,
            "a kill-feed-like death without current perception stays silent");
        memory.ObserveLiving("old", 50, true, true, false);
        check(!memory.ObserveDeath("old", 60.01f, true, true, true, false).Exists,
            "a corpse found long after the living witness does not become a fresh kill report");

        memory.ObserveHit("knife", 70, true, true, true, true, true, true);
        CombatReport knife = memory.ObserveDeath("knife", 71, true, true, true, true);
        check(knife.Kind == CombatReportKind.KnifeKill,
            "a visible death following this player's real knife hit selects KnifeKill over ScavDown");
        memory.ObserveLiving("not-mine", 80, true, true, false);
        memory.ObserveHit("not-mine", 80.2f, true, true, false, true, true, false);
        check(memory.ObserveDeath("not-mine", 81, true, true, true, false).Kind == CombatReportKind.EnemyDown,
            "another actor's knife hit cannot be credited to the local player");
        memory.ObserveHit("expired-knife", 90, true, true, true, true, true, false);
        check(memory.ObserveDeath("expired-knife", 91.51f, true, true, true, false).Kind == CombatReportKind.EnemyDown,
            "old knife evidence cannot relabel a later death");

        check(CombatReportRules.Priority(CombatReportKind.KnifeKill) > CombatReportRules.Priority(CombatReportKind.ScavDown)
            && CombatReportRules.Priority(CombatReportKind.ScavDown) > CombatReportRules.Priority(CombatReportKind.EnemyHit),
            "one event chooses the strongest exact factual meaning");
        check(CombatReportRules.ExactPhrase(CombatReportKind.ScavDown, true) == 76
            && CombatReportRules.ExactPhrase(CombatReportKind.ScavDown, false) == 0,
            "a missing exact ScavDown bank stays silent instead of falling back to EnemyDown");

        memory.Clear();
        memory.ObserveLiving("rescue", 100, true, true, false, reportable: false);
        check(!memory.ObserveHostileDamage("rescue", 100.1f, true, true, true, 8, false, false, 20).Exists,
            "ordinary visible hostile damage records the rescue premise without inventing a sniper");
        memory.ObserveTeammateHit("rescue", 2, 101, true, true, true, true, true, 6);
        CombatReport goodWork = memory.ObserveDeath("rescue", 102, true, true, true, false);
        check(goodWork.Kind == CombatReportKind.GoodWork && goodWork.Actor == 2 && memory.Valid(goodWork, 102),
            "GoodWork binds harm, the perceived human teammate's real counter-hit and visible death on one target");
        memory.RemovePeer(2);
        check(!memory.Valid(goodWork, 102),
            "teammate departure invalidates a queued GoodWork line before playback");
        check(CombatReportRules.ExactPhrase(CombatReportKind.GoodWork, true) == 66
            && CombatReportRules.ExactPhrase(CombatReportKind.GoodWork, false) == 0,
            "missing GoodWork audio consumes the exact outcome without falling back to a generic down line");

        memory.Clear();
        memory.ObserveLiving("arbitrary", 110, true, true, false, reportable: false);
        memory.ObserveTeammateHit("arbitrary", 2, 111, true, true, true, true, true, 20);
        check(memory.ObserveDeath("arbitrary", 112, true, true, true, false).Kind == CombatReportKind.EnemyDown,
            "an arbitrary teammate kill is not praised when that enemy never hurt the local player");
        memory.Clear();
        memory.ObserveLiving("hurt-us", 120, true, true, false, reportable: false);
        memory.ObserveLiving("other-target", 120, true, true, false, reportable: false);
        memory.ObserveHostileDamage("hurt-us", 120.1f, true, true, true, 6, false, false, 10);
        memory.ObserveTeammateHit("other-target", 2, 121, true, true, true, true, true, 20);
        check(memory.ObserveDeath("hurt-us", 122, true, true, true, false).Kind == CombatReportKind.EnemyDown,
            "damage to another enemy cannot be credited as covering the threat that hurt us");
        memory.Clear();
        memory.ObserveLiving("unseen-peer", 130, true, true, false, reportable: false);
        memory.ObserveHostileDamage("unseen-peer", 130.1f, true, true, true, 6, false, false, 10);
        memory.ObserveTeammateHit("unseen-peer", 2, 131, true, true, true, true, false, 20);
        check(memory.ObserveDeath("unseen-peer", 132, true, true, true, false).Kind == CombatReportKind.EnemyDown,
            "an unperceived teammate's replicated damage does not create a GoodWork relationship");
        memory.Clear();
        memory.ObserveLiving("departed-peer", 140, true, true, false, reportable: false);
        memory.ObserveHostileDamage("departed-peer", 140.1f, true, true, true, 6, false, false, 10);
        memory.ObserveTeammateHit("departed-peer", 3, 141, true, true, true, true, true, 20);
        memory.RemovePeer(3);
        check(memory.ObserveDeath("departed-peer", 142, true, true, true, false).Kind == CombatReportKind.EnemyDown,
            "a departed or replaced teammate cannot retain rescue credit");
        memory.Clear();
        memory.ObserveLiving("late-rescue", 150, true, true, false, reportable: false);
        memory.ObserveHostileDamage("late-rescue", 150.1f, true, true, true, 6, false, false, 10);
        memory.ObserveTeammateHit("late-rescue", 2, 151, true, true, true, true, true, 20);
        check(memory.ObserveDeath("late-rescue", 154.01f, true, true, true, false).Kind == CombatReportKind.EnemyDown,
            "a much later death cannot inherit old teammate rescue damage");
        memory.Clear();
        string[] fourThreats = { "peer-cap-a", "peer-cap-b", "peer-cap-c", "peer-cap-d" };
        for (int i = 0; i < fourThreats.Length; i++)
        {
            float at = 160 + i * .1f;
            memory.ObserveLiving(fourThreats[i], at, true, true, false, reportable: false);
            memory.ObserveHostileDamage(fourThreats[i], at + .01f, true, true, true, 5, false, false, 10);
            memory.ObserveTeammateHit(fourThreats[i], 10 + i, at + .02f, true, true, true, true, true, 5);
        }
        check(memory.ObserveDeath("peer-cap-a", 160.5f, true, true, true, false).Kind == CombatReportKind.EnemyDown,
            "four threats still retain at most three distinct teammate rescue identities");

        memory.Clear();
        check(!memory.ObserveFriendlyDamage(2, 200, true, false, 30).Exists
            && !memory.ObserveFriendlyDamage(2, 200, false, true, 30).Exists
            && !memory.ObserveFriendlyDamage(2, 200, true, true, 0).Exists,
            "HoldFire requires a confirmed same-team human who caused real positive damage");
        check(!memory.ObserveFriendlyDamage(2, 201, true, true, 4).Exists,
            "one small friendly graze remains silent");
        CombatReport hold = memory.ObserveFriendlyDamage(2, 201.5f, true, true, 4);
        check(hold.Kind == CombatReportKind.HoldFire && hold.Actor == 2 && memory.Valid(hold, 201.5f),
            "two real friendly hits in one short window support one HoldFire command");
        check(!memory.ObserveFriendlyDamage(2, 202, true, true, 30).Exists,
            "the same teammate cannot repeat HoldFire during its bounded cooldown");
        CombatReport reusedIdentity = memory.ObserveFriendlyDamage(2, 202.1f, true, true, 18, 0, 2);
        check(reusedIdentity.Exists && !memory.Valid(hold, 202.1f),
            "a new occupant generation for a reused actor id resets cooldown and invalidates old fire-discipline evidence");
        CombatReport severeFriendly = memory.ObserveFriendlyDamage(3, 203, true, true, 18);
        check(severeFriendly.Kind == CombatReportKind.HoldFire,
            "one materially damaging friendly hit can demand immediate fire discipline");
        check(CombatReportRules.ExactPhrase(CombatReportKind.HoldFire, true) == 42
            && CombatReportRules.ExactPhrase(CombatReportKind.HoldFire, false) == 0,
            "missing HoldFire audio stays silent without substituting FriendlyFire or another command");
        CombatReport oldestPeer = hold;
        memory.ObserveFriendlyDamage(4, 204, true, true, 18);
        memory.ObserveFriendlyDamage(5, 205, true, true, 18);
        check(!memory.Valid(oldestPeer, 205),
            "the fixed three-teammate cache evicts old fire-discipline state instead of growing");

        memory.Clear();
        check(!memory.ObserveHostileDamage("hidden-sniper", 300, true, false, true, 10, true, false, 20).Exists,
            "a weapon classification cannot reveal an attacker that was never perceived");
        memory.ObserveLiving("sniper", 301, true, true, false, reportable: false);
        CombatReport sniper = memory.ObserveHostileDamage("sniper", 301.2f, true, true, true, 10, true, false, 20);
        check(sniper.Kind == CombatReportKind.SniperThreat && memory.Valid(sniper, 302),
            "a real visible attacker with a reliable sniper weapon classification supports SniperPhrase");
        check(!memory.ObserveHostileDamage("sniper", 301.5f, true, true, true, 10, true, false, 20).Exists,
            "repeated damage cannot multiply sniper warnings");
        check(CombatReportRules.ExactPhrase(CombatReportKind.SniperThreat, true) == 71
            && CombatReportRules.ExactPhrase(CombatReportKind.SniperThreat, false) == 0,
            "missing SniperPhrase audio is consumed without degrading to a generic contact line");
        memory.Clear();
        memory.ObserveLiving("rifleman", 310, true, true, false, reportable: false);
        check(!memory.ObserveHostileDamage("rifleman", 310.1f, true, true, true, 10, false, true, 79.9f).Exists
            && !memory.ObserveHostileDamage("rifleman", 310.2f, true, true, true, 10, false, false, 200).Exists,
            "an ordinary weapon below the explicit long-range threshold is not called a sniper");
        CombatReport longRange = memory.ObserveHostileDamage("rifleman", 311, true, true, true, 10, false, true, 80);
        check(longRange.Kind == CombatReportKind.SniperThreat,
            "an actual attacker with explicit eighty-metre ranged-shot evidence supports the warning");
        memory.Clear();
        memory.ObserveLiving("recent-sniper", 320, true, true, false, reportable: false);
        check(memory.ObserveHostileDamage("recent-sniper", 321.9f, true, false, true, 10, true, false, 20).Kind
                == CombatReportKind.SniperThreat,
            "a clearly perceived attacker remains attributable across the short damage callback delay");
        memory.Clear();
        memory.ObserveLiving("stale-sniper", 330, true, true, false, reportable: false);
        check(!memory.ObserveHostileDamage("stale-sniper", 332.01f, true, false, true, 10, true, false, 20).Exists,
            "old target memory cannot label a background attacker as a sniper");
        memory.Clear();
        memory.ObserveLiving("unconfirmed-sniper", 340, true, true, false, reportable: false);
        check(!memory.ObserveHostileDamage("unconfirmed-sniper", 340.1f, true, true, false, 10, true, false, 20).Exists
            && !memory.ObserveHostileDamage("unconfirmed-sniper", 340.2f, true, true, true, float.NaN, true, false, 20).Exists,
            "unconfirmed attackers and invalid damage cannot create sniper warnings");

        memory.Clear();
        for (int i = 0; i < CombatReportMemory.Capacity; i++)
            memory.ObserveLiving("slot-" + i, 100 + i, true, true, false);
        memory.ObserveLiving("fifth", 105, true, true, false);
        check(!memory.ObserveDeath("slot-0", 106, true, true, true, false).Exists,
            "the fixed four-target cache evicts old evidence instead of growing with the raid");
        CombatReport oldIdentity = memory.ObserveHit("fifth", 106, true, true, true, true, false, false);
        for (int i = 0; i < CombatReportMemory.Capacity; i++)
            memory.ObserveLiving("turnover-" + i, 107 + i, true, true, false);
        memory.ObserveLiving("fifth", 112, true, true, false);
        memory.ObserveHit("fifth", 112, true, true, true, true, false, false);
        check(!memory.Valid(oldIdentity, 112),
            "eviction and identity reuse cannot revive an older queued report version");

        memory.Clear();
        memory.ObserveLiving("steady", 200, true, true, false);
        var benchmark = System.Diagnostics.Stopwatch.StartNew();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++)
        {
            memory.ObserveLiving("steady", 200 + i * 0.01f, true, true, false);
            memory.ObserveHit("steady", 200 + i * 0.01f, true, true, true, true, false, false);
            memory.ObserveHostileDamage("steady", 200 + i * 0.01f, true, true, true, 1, false, false, 10);
            memory.ObserveTeammateHit("steady", 2, 200 + i * 0.01f, true, true, true, true, true, 1);
            memory.ObserveFriendlyDamage(2, 200 + i * 0.01f, true, true, 1);
        }
        long allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
        benchmark.Stop();
        check(allocation == 0,
            "one hundred thousand combat-report updates allocate no managed memory");
        Console.WriteLine($"Combat report benchmark: 100,000 mixed threat/teammate updates in {benchmark.Elapsed.TotalMilliseconds:F2} ms, {allocation} managed bytes.");
    }
}
