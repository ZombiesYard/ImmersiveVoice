using ImmersiveVoice;

internal static class RaidRegressionScenarios
{
    private static VoiceCue Health(InjuryLevel severity, string phrase = "hurt", bool acute = true, bool heavy = false)
        => new() { Key = "health:" + phrase, Group = "health", Intent = phrase == "bleed" ? VoiceIntent.Bleeding : VoiceIntent.Injury,
            Repeat = 20, GroupGap = 20, Urgent = true, AcuteHealth = acute, HealthSeverity = severity, HeavyBleeding = heavy };
    private static SquadPoint P(float x, float y = 0) => new(x, y, 0);
    private static SquadMemory Cover(out SquadDecision decision)
    {
        var memory = new SquadMemory(); memory.Bind(0);
        memory.Sample(0, 0, P(0), P(4), true); memory.Hit(0, 0, "enemy"); memory.Shot(0.1f, "enemy");
        memory.Sample(0, 0.25f, P(0), P(4), true); memory.Shot(0.4f, "enemy");
        decision = memory.Decide(0.5f, true, true, true);
        return memory;
    }

    public static void Run(Action<bool, string> check)
    {
        var policy = new VoicePolicy();
        var medium = Health(InjuryLevel.Injured, "medium");
        var critical = Health(InjuryLevel.Dying, "critical");
        policy.Spoken(782.689f, 2.49f, 1.5f, true, false, 3, medium);
        policy.Spoken(790.428f, 1.04f, 1.5f, false, true, 3, null);
        check(policy.BlockReason(critical, 791.304f) == "SpeechPlaying", "raid critical escalation still waits for the native pain clip");
        check(policy.HealthEscalation(critical, 791.931f) && policy.CanUse(critical, 791.931f),
            "raid request 42 can report Dying after pain ends instead of expiring behind the 20-second wound cooldown");
        var queue = new ReactionQueue();
        var wound = new Reaction { Kind = ReactionKind.Injury, Cue = critical, Priority = 102, ReadyAt = 791.304f, ExpiresAt = 797.513f };
        queue.Offer(wound, 791.304f);
        queue.Offer(new Reaction { Kind = ReactionKind.Ammo, Priority = 96, ReadyAt = 791, ExpiresAt = 797,
            Cue = new VoiceCue { Key = "ammo", Group = "ammo", Urgent = true } }, 791.304f);
        check(queue.Next(791.931f, _ => true, r => policy.CanUse(r.Cue, 791.931f)) == wound,
            "the real raid's critical wound takes precedence over the waiting empty-weapon report");
        policy.Attempt(791.931f);
        check(policy.BlockReason(critical, 793) == "PlaybackBackoff", "escalation cannot bypass a temporary playback failure backoff");
        check(policy.HealthEscalation(critical, 794) && policy.CanUse(critical, 794),
            "an unconfirmed attempt does not consume the wound upgrade");
        policy.Spoken(794, 1.85f, 8, true, false, 3, critical);
        check(!policy.HealthEscalation(critical, 797) && policy.BlockReason(critical, 797) == "RecentSubject",
            "confirmed critical speech consumes the exception so subsequent hit callbacks cannot repeat it");
        check(policy.BlockReason(Health(InjuryLevel.Dying, "critical", heavy: true), 797) == "RecentSubject",
            "new bleeding cannot repeat the same dominant critical phrase when that phrase does not express the bleed");
        check(policy.BlockReason(Health(InjuryLevel.Injured, "arm"), 797) == "RecentCategory",
            "a new ordinary fracture does not bypass the recent critical report");
        policy = new VoicePolicy(); policy.Spoken(10, 1, 8, true, false, 3, medium);
        check(policy.BlockReason(Health(InjuryLevel.Injured, "leg"), 12) == "RecentCategory",
            "same severity with a different wound phrase retains category deduplication");
        check(policy.BlockReason(Health(InjuryLevel.Dying, "critical", acute: false), 12) == "RecentCategory",
            "passive and recovery health hints cannot claim the acute escalation exception");
        var severe = Health(InjuryLevel.BadlyInjured, "severe");
        check(policy.CanUse(severe, 12), "a genuinely severe acute injury can supersede a mild wound report");
        policy.Spoken(12, 1, 8, true, false, 3, severe);
        check(policy.CanUse(critical, 14), "a later distinct progression to Dying can supersede the severe report once");
        policy.Manual(14, 3);
        check(policy.BlockReason(critical, 15) == "ManualPriority", "wound escalation always respects explicit manual silence");
        policy = new VoicePolicy();
        var bleed = Health(InjuryLevel.Injured, "bleed");
        var heavy = Health(InjuryLevel.Injured, "bleed", heavy: true);
        policy.Spoken(10, 1, 8, true, false, 3, bleed);
        check(policy.CanUse(heavy, 12), "new heavy bleeding may reuse its exact phrase despite the old subject timer");
        policy.Spoken(12, 1, 8, true, false, 3, heavy);
        check(policy.BlockReason(heavy, 14) == "RecentSubject", "heavy bleeding does not continuously bypass cooldown");
        policy.Spoken(14, 1, 8, true, false, 3, bleed);
        check(!policy.HealthEscalation(heavy, 16), "a lower-health report cannot re-arm an already expressed upgrade in the same cooldown");
        check(policy.CanUse(heavy, 35), "normal health repetition remains possible when its original timers expire");
        policy = new VoicePolicy(); policy.Spoken(10, 1, 8, false, false, 3, medium);
        check(policy.BlockReason(critical, 12) == "ManualPriority" && policy.CanUse(critical, 15),
            "manual wound speech supplies the shared baseline while keeping the manual priority window");
        check(!policy.HealthEscalation(new VoiceCue { Group = "ammo", AcuteHealth = true, HealthSeverity = InjuryLevel.Dying }, 15),
            "the wound exception is restricted to the health category");

        var awareness = new SquadAwareness();
        check(!awareness.ReportNearby(10, P(0)), "report memory cannot invent a never-observed companion");
        awareness.ObserveReport(10, P(0), P(12));
        check(awareness.ReportNearby(12.49f, P(0)) && !awareness.ReportNearby(12.51f, P(0)),
            "a brief sight gap retains team report context for at most 2.5 seconds without renewal by reads");
        check(!awareness.Nearby(11, false, 4, 0), "remembered report context does not create fresh perception for movement or covering");
        var context = new BarkContext { Mode = VoiceMode.FikaClient, RecentTeammate = true };
        check(SemanticVoice.EnemyDown(context, false) == VoiceIntent.TacticalEnemyDown
            && SemanticVoice.RejectionReason(VoiceIntent.TacticalEnemyDown, context, 0) == null,
            "a witnessed death can be reported across a companion's brief sight gap");
        var medical = context; medical.Injury = InjuryLevel.BadlyInjured;
        check(!context.TeamInformation && !MedicalRequestPolicy.Eligible(medical),
            "report memory cannot replace current medical recipient perception");
        context.RecentlyHit = true;
        check(SemanticVoice.EnemyDown(context, false) == VoiceIntent.TacticalEnemyDown
            && !SemanticVoice.StillValid(VoiceIntent.TacticalEnemyDown, context, 0),
            "recent companion memory preserves a candidate but cannot speak over the recent-hit window");
        context.RecentlyHit = false; context.Mode = VoiceMode.Solo;
        check(SemanticVoice.EnemyDown(context, false) != VoiceIntent.TacticalEnemyDown, "solo play cannot inherit team report semantics");
        context.Mode = VoiceMode.FikaClient; context.RecentTeammate = false;
        check(SemanticVoice.RejectionReason(VoiceIntent.TacticalEnemyDown, context, 0) == "TeammateNoLongerNearby",
            "report expiry still cancels an old team announcement");
        awareness.ObserveReport(10, P(0), P(12)); awareness.ObserveReport(10.25f, P(0), P(12.2f));
        check(awareness.ReportNearby(12.7f, P(0)), "a freshly perceived companion crossing the 12-metre edge has limited range hysteresis");
        awareness.ObserveReport(11, P(0), P(14.1f));
        check(!awareness.ReportNearby(11, P(0)), "observed departure beyond 14 metres clears report continuity immediately");
        awareness.ObserveReport(12, P(0), P(13));
        check(!awareness.ReportNearby(12, P(0)), "the outer range cannot acquire an unfamiliar companion");
        awareness.ObserveReport(13, P(0), P(4));
        check(!awareness.ReportNearby(14, P(5)), "moving far from the last observation cannot preserve a hidden companion indefinitely");
        check(!awareness.ReportNearby(14, P(0, 2)), "a floor change invalidates the old report context");
        awareness.Clear();
        check(!awareness.ReportNearby(14, P(0)), "pause, roster replacement and unavailability discard report memory");
        awareness.ObserveReport(15, P(float.NaN), P(4));
        check(!awareness.ReportNearby(15, P(0)), "invalid transform samples cannot create report memory");

        var squad = Cover(out var decision);
        check(decision.Action == SquadAction.Covering && squad.RejectionReason(decision, 0.5f, true) == null,
            "valid covering retains its original behaviour with detailed diagnostics enabled");
        check(squad.RejectionReason(decision, 0.6f, false) == "SquadWeaponUnavailable", "cover cancellation identifies a busy or unavailable weapon");
        squad.TargetDown("enemy");
        check(squad.RejectionReason(decision, 0.608f, true) == "SquadCoverTargetDown",
            "the raid's target death cancels Covering with an explicit cause instead of generic behaviour changed");
        squad = Cover(out decision); squad.Shot(0.6f, "other");
        check(squad.RejectionReason(decision, 0.6f, true) == "SquadCoverTargetChanged", "switching to a different enemy is distinguished from target death");
        squad = Cover(out decision); squad.Shot(0.6f, null);
        check(squad.RejectionReason(decision, 0.6f, true) == "SquadCoverTargetUnobserved", "firing without a visible target is distinguished from target death");
        squad = Cover(out decision);
        for (float t = 0.5f; t <= 2.5f; t += 0.25f) squad.Sample(0, t, P(0), P(4), true);
        check(squad.RejectionReason(decision, 2.5f, true) == "SquadCoverFireExpired", "ceased fire has its own cancellation reason");
        squad = Cover(out decision); squad.Suspend(0.6f);
        check(squad.RejectionReason(decision, 0.6f, true) == "SquadObservationReset", "suspension is distinguished from ordinary movement changes");
        squad = Cover(out decision); squad.Unbind(0); squad.Bind(0);
        check(squad.RejectionReason(decision, 0.6f, true) == "SquadPeerReplaced", "slot reuse identifies the replaced peer without logging identities");
        squad = new SquadMemory(); squad.Bind(0);
        for (float t = 0; t <= 1; t += 0.25f)
        { squad.Sample(0, t, P(0), P(4), true); squad.OwnAction(t, SquadOwnAction.Reloading, true); }
        decision = squad.Decide(1, true, true, false);
        squad.OwnAction(1.1f, SquadOwnAction.None, true);
        check(decision.Action == SquadAction.RequestCover && squad.RejectionReason(decision, 1.1f, true) == "SquadActionEnded",
            "a completed reload cancels its cover request with the actual action-ended cause");
    }
}
