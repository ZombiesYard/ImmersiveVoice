using ImmersiveVoice;

internal static class TreatmentScenarios
{
    public static void Run(Action<bool, string> check)
    {
        TreatmentSnapshot State(float hp = 200, InjuryLevel injury = InjuryLevel.BadlyInjured,
            int bleeds = 3, int fractures = 0, int heavy = 0)
            => new() { Health = hp, Injury = injury, Bleeds = bleeds, Fractures = fractures, HeavyBleeds = heavy };
        var context = new BarkContext { Combat = CombatPhase.Recovering, Injury = InjuryLevel.BadlyInjured,
            Healing = true, Bleeding = true, DamageVersion = 1 };
        var before = State();
        var improved = State(240, InjuryLevel.Injured, bleeds: 1);
        var treatment = new TreatmentMemory();
        check(treatment.Begin(10, context, before, 1), "recent combat and actual wounds can start a treatment observation");
        check(treatment.Observe(20, true, context, improved, 1) == TreatmentOutcome.None,
            "health improvement while medicine remains in hands is not a completion announcement");
        context.Healing = false; context.Injury = InjuryLevel.Injured; context.Combat = CombatPhase.Calm;
        check(treatment.Observe(30, false, context, improved, 1) == TreatmentOutcome.None
            && treatment.Observe(32.9f, false, context, improved, 1) == TreatmentOutcome.None,
            "putting medicine away waits three quiet seconds rather than immediately declaring success");
        check(treatment.Observe(33, false, context, improved, 1) == TreatmentOutcome.RemainingConcern
            && SemanticVoice.AfterTreatment(context) && SemanticVoice.Injury(context, false) == VoiceIntent.Bleeding,
            "long treatment can report a remaining bleed after combat memory expires without a generic recovery line");
        check(treatment.Observe(34, false, context, improved, 1) == TreatmentOutcome.None,
            "one completed observation produces at most one decision");
        context.Combat = CombatPhase.Recovering; context.Healing = true;
        check(!treatment.Begin(35, context, improved, 1),
            "missing clips or queued-line expiry do not grant another treatment ending in the same exchange");
        check(treatment.Begin(36, context, improved, 2), "a later exchange can support a new treatment observation");
        treatment.Cancel();

        treatment = new TreatmentMemory(); treatment.Begin(10, context, before, 1);
        context.Healing = false; context.Bleeding = false; context.Injury = InjuryLevel.Healthy;
        var healed = State(440, InjuryLevel.Healthy, 0);
        treatment.Observe(20, false, context, healed, 1);
        check(treatment.Observe(23, false, context, healed, 1) == TreatmentOutcome.NoRemainingConcern
            && !SemanticVoice.AfterTreatment(context), "complete wound resolution ends quietly without Ready or a success phrase");
        context.Injury = InjuryLevel.Injured;
        check(!SemanticVoice.AfterTreatment(context), "minor remaining HP loss alone does not justify another post-treatment complaint");

        context.Injury = InjuryLevel.BadlyInjured; context.Bleeding = true; context.Healing = true;
        treatment = new TreatmentMemory(); treatment.Begin(10, context, before, 1);
        context.Healing = false;
        treatment.Observe(11, false, context, before, 1);
        check(treatment.Observe(14, false, context, before, 1) == TreatmentOutcome.NoObservedImprovement,
            "an interrupted or ineffective use with unchanged health creates no after-treatment voice");
        context.Healing = true;
        check(treatment.Begin(15, context, before, 1), "an ineffective attempt does not prevent a later actual improvement");
        check(!State(200.1f).ImprovedFrom(before), "tiny health noise alone is not treatment progress");
        check(State(210).ImprovedFrom(before), "partial HP restoration is observed progress without asserting full application");
        check(State(200, bleeds: 1).ImprovedFrom(before), "removing one of two limb bleeds is real partial progress");
        check(State(200, bleeds: 0, fractures: 1).ImprovedFrom(State(200, bleeds: 0, fractures: 3)),
            "fixing one of two fractures remains visible even while another arm is still broken");
        check(State(200, bleeds: 0, heavy: 0).ImprovedFrom(State(200, bleeds: 0, heavy: 1)),
            "heavy bleeding removal is recognized independently of HP gain");
        check(State(200, bleeds: 4).NewConcernSince(before), "a new bleed on another limb is not confused with healing the original limb");
        check(State(240, bleeds: 1, heavy: 1).NewConcernSince(before), "new heavy bleeding invalidates an old treatment narrative despite HP gain");
        check(State(240, InjuryLevel.Dying).NewConcernSince(before), "a worsening health grade is a new survival concern");
        foreach (float hp in new[] { float.NaN, float.PositiveInfinity, -1f })
            check(!State(hp).ImprovedFrom(before), "invalid HP " + hp + " cannot prove improvement");

        treatment = new TreatmentMemory(); treatment.Begin(10, context, before, 1);
        context.Healing = false; treatment.Observe(20, false, context, improved, 1);
        context.Healing = true;
        check(treatment.Begin(22, context, improved, 1) && treatment.Before.Health == before.Health,
            "back-to-back medicines retain one baseline across a short gap");
        context.Healing = false;
        check(treatment.Observe(30, false, context, improved, 1) == TreatmentOutcome.None
            && treatment.Observe(33, false, context, improved, 1) == TreatmentOutcome.RemainingConcern,
            "a consecutive medicine sequence waits until its final quiet gap for one decision");
        treatment = new TreatmentMemory(); context.Healing = true;
        treatment.Begin(10, context, before, 1);
        context.Healing = false; treatment.Observe(20, false, context, improved, 1);
        context.Combat = CombatPhase.Calm; context.Healing = true;
        check(!treatment.Begin(40, context, improved, 1), "a long unsampled gap cannot attach calm treatment to old combat");
        context.Combat = CombatPhase.Recovering;
        treatment = new TreatmentMemory(); treatment.Begin(10, context, before, 1);
        treatment.Begin(80, context, improved, 1);
        check(treatment.Observe(100, true, context, improved, 1) == TreatmentOutcome.Expired,
            "continuing treatment cannot extend the ninety-second observation window indefinitely");

        treatment = new TreatmentMemory(); treatment.Begin(10, context, before, 1);
        context.DamageVersion++;
        check(treatment.Observe(12, true, context, improved, 1) == TreatmentOutcome.ContextChanged,
            "another hit cancels treatment continuity even while medicine remains in hands");
        context.DamageVersion = 1;
        treatment = new TreatmentMemory(); treatment.Begin(10, context, before, 1);
        context.Healing = false; context.Combat = CombatPhase.Calm;
        check(treatment.Observe(20, false, context, improved, 2) == TreatmentOutcome.ContextChanged,
            "combat that resumes and ends between ticks cannot revive the prior treatment ending");
        context.Combat = CombatPhase.Recovering; context.Healing = true;
        treatment = new TreatmentMemory(); treatment.Begin(10, context, before, 1);
        check(treatment.Observe(20, true, context, State(240, heavy: 1), 1) == TreatmentOutcome.ContextChanged,
            "a new observed wound cancels the sequence without requiring a combat-damage callback");
        treatment = new TreatmentMemory(); treatment.Begin(10, context, before, 1);
        context.Healing = false; context.Combat = CombatPhase.Engaged;
        check(treatment.Observe(12, false, context, improved, 1) == TreatmentOutcome.ContextChanged,
            "returning to an active fight cancels a prospective quiet treatment ending");
        context.Combat = CombatPhase.Recovering; context.Healing = true;
        treatment = new TreatmentMemory(); treatment.Begin(10, context, before, 1);
        check(treatment.Observe(12, false, context, improved, 1) == TreatmentOutcome.ContextChanged,
            "switching from medicine to another consumable is not a quiet completed recovery");
        treatment = new TreatmentMemory(); treatment.Begin(10, context, before, 1);
        int version = treatment.Version; treatment.Cancel();
        check(!treatment.Tracking && treatment.Version != version,
            "manual input or renewed firing invalidates both observation and pending treatment identity");
        context.Healing = false;
        check(treatment.Observe(50, false, context, improved, 1) == TreatmentOutcome.None,
            "cancelled observations do not reappear when health later improves");
        treatment.Clear(); context.Combat = CombatPhase.Calm;
        check(!treatment.Begin(60, context, before, 1), "menu resume or routine calm healing cannot fabricate a prior combat narrative");
        context.Combat = CombatPhase.Recovering;
        check(!treatment.Begin(60, context, State(float.NaN), 1), "an invalid initial health reading keeps the feature silent");

        context.Injury = InjuryLevel.Injured; context.Bleeding = false; context.ArmFracture = true;
        check(SemanticVoice.AfterTreatment(context) && SemanticVoice.Injury(context, false) == VoiceIntent.ArmFracture,
            "the remaining current fracture determines the meaning after bleeding has stopped");
        check(!SemanticVoice.AfterTreatment(context, medicalRequestPending: true),
            "a pending medical supply request replaces residual-wound commentary rather than stacking both");
        context.RecentlyHit = true;
        check(!SemanticVoice.AfterTreatment(context), "fresh damage takes precedence over a residual-wound comment");
        context.RecentlyHit = false; context.Healing = true;
        check(!SemanticVoice.AfterTreatment(context), "another medicine use blocks queued residual-wound speech");

        var queue = new ReactionQueue();
        var old = new Reaction { Kind = ReactionKind.AfterTreatment, Priority = 30, ReadyAt = 10, ExpiresAt = 22,
            Cue = new VoiceCue { Key = "health:old", Group = "health" } };
        queue.Offer(old, 10);
        var fresh = new Reaction { Kind = ReactionKind.Injury, Priority = 98, ReadyAt = 11, ExpiresAt = 18,
            Cue = new VoiceCue { Key = "health:new", Group = "health", Urgent = true } };
        queue.Offer(fresh, 11); queue.RemoveKind(ReactionKind.AfterTreatment, "DamageAfterTreatment");
        check(queue.Count == 1 && queue.Next(11, _ => true, _ => true) == fresh,
            "clearing old treatment speech preserves a newer urgent injury in the shared health category");
        var policy = new VoicePolicy(); old.Cue.Repeat = old.Cue.GroupGap = 20;
        policy.Spoken(10, 2, 8, true, false, 12, old.Cue);
        check(!policy.CanUse(fresh.Cue, 29) && policy.CanUse(fresh.Cue, 30),
            "treatment and injury speech retain the existing shared health cooldown");
        policy = new VoicePolicy();
        check(policy.CanUse(old.Cue, 10), "choosing silence or finding no audio creates no playback cooldown");
    }
}
