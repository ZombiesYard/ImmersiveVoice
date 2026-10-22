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
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++)
        {
            memory.ObserveLiving("steady", 200 + i * 0.01f, true, true, false);
            memory.ObserveHit("steady", 200 + i * 0.01f, true, true, true, true, false, false);
        }
        check(GC.GetAllocatedBytesForCurrentThread() == allocated,
            "one hundred thousand combat-report updates allocate no managed memory");
    }
}
