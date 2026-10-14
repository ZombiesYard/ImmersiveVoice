using ImmersiveVoice;

internal static class HealthSpeechScenarios
{
    public static void Run(Action<bool, string> check)
    {
        var c = new BarkContext { Injury = InjuryLevel.Injured };
        var memory = new HealthSpeechMemory();
        memory.Observe(350, c);
        check(memory.BlockReason(HealthCue.Injury, c, 350) == null,
            "an unreported actual wound may use its matching native hint");
        memory.Consider(); // Actual confirmed automatic or manual report.
        int episode = memory.Version;
        foreach (float now in new[] { 400f, 425.065f, 445.154f, 465.156f, 485.22f, 509.254f, 529.745f })
        {
            memory.Observe(now, c);
            check(memory.BlockReason(HealthCue.Injury, c, now) == "SameHealthEpisode" && memory.Version == episode,
                "persistent wound at " + now + " cannot repeat after the old twenty-second cooldown");
        }
        memory.Pause(); memory.Observe(600, c);
        check(memory.BlockReason(HealthCue.Injury, c, 600) == "SameHealthEpisode",
            "menu, silence or VOIP suspension does not reset a wound decision");
        c.Bleeding = true; memory.Observe(601, c);
        check(memory.Version > episode && memory.BlockReason(HealthCue.Bleeding, c, 601) == null,
            "new bleeding reopens one factual wound episode");
        memory.Missing();
        check(memory.BlockReason(HealthCue.Injury, c, 602) == "SameHealthEpisode",
            "a missing bleeding bank cannot be filled by another injury hint");
        c.HeavyBleeding = true; memory.Observe(603, c);
        check(!memory.Considered, "new heavy bleeding remains a meaningful worsening");
        memory.Consider(); c.ArmFracture = true; memory.Observe(604, c);
        check(memory.BlockReason(HealthCue.ArmFracture, c, 604) == null, "a newly broken arm reopens its actual hint");
        memory.Consider(); c.LegFracture = true; memory.Observe(605, c);
        check(memory.BlockReason(HealthCue.LegFracture, c, 605) == null, "a newly broken leg reopens its actual hint");
        memory.Consider(); c.Injury = InjuryLevel.BadlyInjured; memory.Observe(606, c);
        check(memory.BlockReason(HealthCue.SevereInjury, c, 606) == null, "worsening health grade can report once");
        memory.Consider(); c.Injury = InjuryLevel.Dying; memory.Observe(607, c);
        check(memory.BlockReason(HealthCue.CriticalInjury, c, 607) == null, "critical deterioration is not muted by an older wound decision");
        check(memory.BlockReason(HealthCue.Injury, c, 607) == "HealthHintNoLongerMatches",
            "an outdated mild hint cannot describe a currently critical injury");

        memory = new HealthSpeechMemory(); c = new BarkContext { Injury = InjuryLevel.Injured };
        memory.Observe(10, c);
        c.RecentlyHit = true;
        check(memory.BlockReason(HealthCue.Injury, c, 10) == "HealthEventPreferred",
            "fresh damage aggregation takes precedence over a passive hint");
        c.RecentlyHit = false;
        check(memory.BlockReason(HealthCue.Injury, c, 11, healthPending: true) == "HealthEventPreferred",
            "a waiting director wound report cannot be overtaken by native Y");
        check(memory.BlockReason(HealthCue.Injury, c, 11, treatmentPending: true) == "TreatmentSettling",
            "verified treatment observation and medical requests have priority");
        check(!memory.Considered, "merely waiting on another event does not consume the passive decision");
        c.Healing = true; memory.Observe(12, c);
        check(memory.BlockReason(HealthCue.Injury, c, 12) == "HealingInProgress" && memory.Considered,
            "starting treatment cancels even a previously eligible passive complaint");
        memory.Observe(100, c);
        check(memory.BlockReason(HealthCue.Injury, c, 100) == "HealingInProgress",
            "long treatment cannot outwait the treatment gate");
        c.Healing = false; memory.Observe(101, c);
        check(memory.BlockReason(HealthCue.Injury, c, 103.9f) == "TreatmentSettling",
            "putting medicine away retains a three-second quiet window");
        check(memory.BlockReason(HealthCue.Injury, c, 104) == "SameHealthEpisode",
            "ineffective treatment does not simply restart the old passive complaint");
        c.Healing = true; memory.Observe(105, c);
        c.Injury = InjuryLevel.BadlyInjured; memory.Observe(106, c);
        check(!memory.Considered && memory.BlockReason(HealthCue.SevereInjury, c, 106) == "HealingInProgress",
            "worsening during treatment is remembered but waits for medicine to be put away");
        c.Healing = false; memory.Observe(110, c);
        check(memory.BlockReason(HealthCue.SevereInjury, c, 112.9f) == "TreatmentSettling"
            && memory.BlockReason(HealthCue.SevereInjury, c, 113) == null,
            "new deterioration becomes eligible only after treatment settles");
        c.Healing = true; memory.Observe(113, c);
        c.Healing = false; memory.Observe(120, c);
        check(memory.BlockReason(HealthCue.SevereInjury, c, 122) == "TreatmentSettling" && memory.Considered,
            "another medicine restarts quiet time and consumes the old passive hint");
        c.Healing = true; memory.Observe(124, c); memory.Pause();
        c.Healing = false; memory.Observe(200, c);
        check(memory.BlockReason(HealthCue.SevereInjury, c, 202) == "TreatmentSettling",
            "resuming after unsampled treatment completion starts a conservative quiet window");

        memory = new HealthSpeechMemory(); c = new BarkContext { Injury = InjuryLevel.BadlyInjured };
        memory.Observe(0, c); memory.Consider(); episode = memory.Version;
        c.Injury = InjuryLevel.Injured; memory.Observe(1, c); memory.Observe(3.9f, c);
        c.Injury = InjuryLevel.BadlyInjured; memory.Observe(4, c);
        check(memory.Considered && memory.Version == episode, "brief health-grade jitter cannot rearm a wound complaint");
        c.Injury = InjuryLevel.Injured; memory.Observe(5, c); memory.Observe(8, c);
        check(memory.Considered, "stable partial improvement alone does not rearm complaints");
        c.Injury = InjuryLevel.BadlyInjured; memory.Observe(9, c);
        check(!memory.Considered, "deterioration after stable partial improvement is a new factual episode");
        memory.Consider(); c.Injury = InjuryLevel.Healthy; memory.Observe(10, c); memory.Observe(14.9f, c);
        check(memory.Considered, "full recovery must be observed for five seconds before reset");
        memory.Pause(); memory.Observe(100, c);
        check(memory.Considered, "time inside a menu does not prove sustained recovery");
        memory.Observe(105, c);
        check(!memory.Considered, "five continuously observed healthy seconds reset the episode");
        c.Injury = InjuryLevel.Injured; memory.Observe(106, c);
        check(memory.BlockReason(HealthCue.Injury, c, 106) == null, "a wound after full recovery can be reported again");
        check(memory.BlockReason(HealthCue.Bleeding, c, 106) == "HealthHintNoLongerMatches"
            && memory.BlockReason(HealthCue.ArmFracture, c, 106) == "HealthHintNoLongerMatches"
            && memory.BlockReason(HealthCue.LegFracture, c, 106) == "HealthHintNoLongerMatches",
            "cleared effects cannot be announced through stale native prompts");
        foreach (VoiceMode mode in Enum.GetValues<VoiceMode>())
        {
            c.Mode = mode; memory.Observe(107, c);
            check(memory.BlockReason(HealthCue.Injury, c, 107) == null,
                mode + " uses the same local wound facts without a teammate prerequisite");
        }
        c.Healing = true; c.RecentlyHit = true; memory.Observe(108, c);
        check(memory.BlockReason(HealthCue.None, c, 108, true, true) == null,
            "the passive wound gate leaves unrelated phrases and urgent warnings unchanged");

        // Benchmark only pure bounded state; this does not measure Unity or log formatting.
        c = new BarkContext { Injury = InjuryLevel.Injured };
        memory = new HealthSpeechMemory(); memory.Observe(0, c); memory.Consider();
        for (int i = 0; i < 10000; i++) { memory.Observe(i, c); memory.BlockReason(HealthCue.Injury, c, i); }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++) { memory.Observe(i, c); memory.BlockReason(HealthCue.Injury, c, i); }
        check(GC.GetAllocatedBytesForCurrentThread() == allocated,
            "one hundred thousand stable wound checks allocate no managed memory after warmup");
    }
}
