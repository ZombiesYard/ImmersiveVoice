using ImmersiveVoice;

internal static class EnemyDownWaitScenarios
{
    private static Reaction Report(float now, BarkContext context, float delay = 0.8f)
        => new() { Kind = ReactionKind.EnemyDown, Priority = 78, ReadyAt = now + delay, ExpiresAt = now + 7,
            DamageVersion = context.DamageVersion, Subject = "witnessed",
            Cue = new VoiceCue { Key = "kill:witnessed", Group = "kill", Repeat = 7, GroupGap = 7,
                Intent = SemanticVoice.EnemyDown(context, true) } };

    private static Reaction Next(ReactionQueue queue, float now, BarkContext context, VoicePolicy policy)
        => queue.Next(now,
            r => (r.InvalidReason = SemanticVoice.PendingRejectionReason(r.Cue.Intent, context, r.DamageVersion)) == null,
            r => SemanticVoice.WaitingReason(r.Cue.Intent, context.RecentlyHit) == null && policy.CanUse(r.Cue, now));

    public static void Run(Action<bool, string> check)
    {
        var context = new BarkContext { Mode = VoiceMode.FikaClient, NearbyTeammate = true,
            Combat = CombatPhase.Recovering, Injury = InjuryLevel.Injured, RecentlyHit = true, DamageVersion = 6 };
        foreach (var mode in new[] { VoiceMode.FikaHost, VoiceMode.FikaClient })
        {
            context.Mode = mode;
            check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.TacticalEnemyDown
                && SemanticVoice.PendingRejectionReason(VoiceIntent.TacticalEnemyDown, context, 6) == null,
                mode + " preserves a witnessed team report during the pre-existing hit window");
            check(SemanticVoice.RejectionReason(VoiceIntent.TacticalEnemyDown, context, 6) == "RecentDamage"
                && SemanticVoice.WaitingReason(VoiceIntent.TacticalEnemyDown, true) == "RecentDamage",
                mode + " still forbids playback until the hit window ends");
        }
        var blocked = context; blocked.NearbyTeammate = false;
        check(SemanticVoice.EnemyDown(blocked, true) == VoiceIntent.Silent, "recent hit without a report recipient stays silent");
        blocked = context; blocked.Mode = VoiceMode.Solo;
        check(SemanticVoice.EnemyDown(blocked, true) == VoiceIntent.Silent, "solo kill reactions do not gain a deferred hit exception");
        foreach (var state in new[] { "severe", "dying", "bleeding", "healing" })
        {
            blocked = context;
            if (state == "severe") blocked.Injury = InjuryLevel.BadlyInjured;
            if (state == "dying") blocked.Injury = InjuryLevel.Dying;
            if (state == "bleeding") blocked.HeavyBleeding = true;
            if (state == "healing") blocked.Healing = true;
            check(SemanticVoice.EnemyDown(blocked, true) == VoiceIntent.Silent
                && SemanticVoice.PendingRejectionReason(VoiceIntent.TacticalEnemyDown, blocked, 6) != null,
                state + " neither creates nor retains a delayed tactical report");
        }
        blocked = context; blocked.Bleeding = blocked.ArmFracture = blocked.LegFracture = true;
        check(SemanticVoice.EnemyDown(blocked, true) == VoiceIntent.TacticalEnemyDown,
            "ordinary wounds do not erase a factual team report");
        check(SemanticVoice.PendingRejectionReason(VoiceIntent.KillTaunt, context, 6) == "RecentDamage"
            && SemanticVoice.PendingRejectionReason(VoiceIntent.KillConfirm, context, 6) == "RecentDamage"
            && SemanticVoice.PendingRejectionReason(VoiceIntent.CombatBark, context, 6) == "RecentDamage",
            "personal confirmations, taunts and combat barks retain strict survival cancellation");

        // Log-derived request 7: the wound clip was still playing when the
        // corpse was witnessed. Model no further damage and both delay extremes;
        // use the later quiet sample after the peer's reload line has ended.
        foreach (float delay in new[] { 0.8f, 2.4f })
        {
            var queue = new ReactionQueue(); var policy = new VoicePolicy();
            policy.Spoken(1166.551f, 2.16f, 8, true, false, 3,
                new VoiceCue { Key = "health:medium", Group = "health", Intent = VoiceIntent.Injury });
            var report = Report(1167.731f, context, delay); queue.OfferEnding(report, 1167.731f);
            check(Next(queue, 1168.6f, context, policy) == null && queue.Count == 1,
                "raid request 7 survives queue inspection while hit protection is still active, delay=" + delay);
            var recovered = context; recovered.RecentlyHit = false;
            check(policy.BlockReason(report.Cue, 1168.9f) == "SpeechPlaying",
                "ending hit protection never interrupts the raid's already playing HurtMedium");
            check(Next(queue, 1171.486f, recovered, policy) == report
                && SemanticVoice.StillValid(report.Cue.Intent, recovered, report.DamageVersion)
                && report.ExpiresAt == 1167.731f + 7,
                "raid request 7 can proceed after protection and wound audio end within its original expiry, delay=" + delay);
            queue.Remove(report);
            check(Next(queue, 1171.6f, recovered, policy) == null, "a consumed report is not rescheduled by the wait logic");
        }

        var p = new VoicePolicy(); var q = new ReactionQueue();
        var pending = Report(10, context); q.OfferEnding(pending, 10);
        var latest = Report(11, context); latest.Subject = "second-witnessed"; q.OfferEnding(latest, 11);
        check(q.Count == 1 && latest.ExpiresAt == 17, "another witnessed death cannot extend the original waiting report's lifetime");
        check(Next(q, 17, context, p) == null && q.Count == 0, "a blocked report expires even if hit protection never clears");

        q = new ReactionQueue(); pending = Report(10, context); q.OfferEnding(pending, 10);
        blocked = context; blocked.DamageVersion++;
        check(SemanticVoice.PendingRejectionReason(pending.Cue.Intent, blocked, pending.DamageVersion) == "DamageAfterRequest",
            "new damage cancels the report instead of restarting its wait");
        blocked.RecentlyHit = false;
        check(Next(q, 13, blocked, p) == null && q.Count == 0 && pending.InvalidReason == "DamageAfterRequest",
            "damage after the request cannot be forgotten merely because its hit window already ended");

        q = new ReactionQueue(); pending = Report(10, context); q.OfferEnding(pending, 10);
        var awareness = new SquadAwareness(); awareness.ObserveReport(10, new(0, 0, 0), new(4, 0, 0));
        blocked = context; blocked.NearbyTeammate = false; blocked.RecentTeammate = awareness.ReportNearby(11, new(0, 0, 0));
        check(Next(q, 11, blocked, p) == null && q.Count == 1,
            "2.10.1 companion memory and the hit wait coexist across a brief sight gap");
        blocked.RecentTeammate = awareness.ReportNearby(12.51f, new(0, 0, 0));
        check(Next(q, 12.51f, blocked, p) == null && q.Count == 0 && pending.InvalidReason == "TeammateNoLongerNearby",
            "report-recipient expiry cancels even while the report is waiting for hit protection");

        q = new ReactionQueue(); pending = Report(10, context); q.OfferEnding(pending, 10);
        context.RecentlyHit = false;
        var wound = new Reaction { Kind = ReactionKind.Injury, Priority = 87, ReadyAt = 10, ExpiresAt = 16,
            Cue = new VoiceCue { Key = "health:medium", Group = "health", Intent = VoiceIntent.Injury, Urgent = true } };
        q.Offer(wound, 10);
        check(Next(q, 13, context, p) == wound, "a ready tactical report continues to yield to acute wound speech");
        q.Remove(wound); p.Manual(13, 3);
        check(Next(q, 14, context, p) == null && q.Count == 1, "deferred reports respect manual priority");
        p.Attempt(16);
        check(Next(q, 16.1f, context, p) == null && q.Count == 1, "deferred reports retain native failure backoff");
        check(Next(q, 18, context, p) == null && q.Count == 0, "backoff cannot extend a deferred report past its expiry");
        q.OfferEnding(Report(20, context), 20); q.Clear();
        check(Next(q, 23, context, new VoicePolicy()) == null, "manual or lifecycle clearing leaves no delayed report to revive");

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++)
        {
            SemanticVoice.PendingRejectionReason(VoiceIntent.TacticalEnemyDown, context, 6);
            SemanticVoice.WaitingReason(VoiceIntent.TacticalEnemyDown, (i & 1) != 0);
        }
        check(GC.GetAllocatedBytesForCurrentThread() == allocated, "one hundred thousand report wait checks allocate no managed memory");
    }
}
