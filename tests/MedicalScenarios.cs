using ImmersiveVoice;

internal static class MedicalScenarios
{
    public static void Run(Action<bool, string> check)
    {
        var context = new BarkContext { Injury = InjuryLevel.BadlyInjured, NearbyTeammate = true };
        check(!MedicalRequestPolicy.Eligible(context), "Solo never requests medical supplies from a friendly NPC");
        foreach (var mode in new[] { VoiceMode.FikaHost, VoiceMode.FikaClient })
        {
            context.Mode = mode;
            check(MedicalRequestPolicy.Eligible(context), mode + " can seek supplies with serious wounds and a visible human teammate");
            context.NearbyTeammate = false;
            check(!MedicalRequestPolicy.Eligible(context), mode + " without a visible nearby audience stays silent");
            context.NearbyTeammate = true;
        }
        context.Injury = InjuryLevel.Injured;
        check(!MedicalRequestPolicy.Eligible(context), "minor injury does not prompt a supply request");
        context.HeavyBleeding = true;
        check(MedicalRequestPolicy.Eligible(context), "heavy bleeding can justify a request before health becomes critical");
        context.Healing = true;
        check(!MedicalRequestPolicy.Eligible(context), "active self-treatment blocks requests regardless of health");
        context.Healing = false; context.RecentlyHit = true;
        check(!MedicalRequestPolicy.Eligible(context), "the immediate damage reaction takes precedence over a request");
        context.RecentlyHit = false; context.HeavyBleeding = false; context.Injury = InjuryLevel.Dying;
        check(MedicalRequestPolicy.Eligible(context), "a living critically injured player may request supplies after the hit settles");

        check(MedicalRequestPolicy.Medicine(true, 10, true) == MedicalSupplies.Available, "a usable medicine with resources suppresses shortage");
        check(MedicalRequestPolicy.Medicine(true, 0, false) == MedicalSupplies.Missing, "an empty medical item is not usable supply");
        check(MedicalRequestPolicy.Medicine(true, 0, true) == MedicalSupplies.Unknown,
            "a zero-resource item the game still considers applicable cannot prove shortage");
        check(MedicalRequestPolicy.Medicine(true, 0.01f, true) == MedicalSupplies.Available, "even a nearly spent usable medicine prevents false shortage");
        check(MedicalRequestPolicy.Medicine(true, 10, false) == MedicalSupplies.Unknown,
            "a temporarily inapplicable medicine is uncertainty, not evidence that the player has none");
        check(MedicalRequestPolicy.Medicine(false, 0, true) == MedicalSupplies.Unknown,
            "stimulants and other medicines without known resource accounting stay conservative");
        foreach (float remaining in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            check(MedicalRequestPolicy.Medicine(true, remaining, false) == MedicalSupplies.Unknown,
                "invalid medicine resource " + remaining + " cannot prove shortage");

        foreach (var supplies in new[] { MedicalSupplies.Available, MedicalSupplies.Unknown })
        {
            var withheld = new MedicalRequestDecision();
            withheld.Observe(10, context, supplies);
            check(!withheld.Observe(100, context, supplies) && !withheld.Decided,
                supplies + " cannot authorize a request after any wait");
        }
        var decision = new MedicalRequestDecision();
        check(!decision.Observe(10, context, MedicalSupplies.Missing)
            && !decision.Observe(12.9f, context, MedicalSupplies.Missing)
            && decision.Observe(13, context, MedicalSupplies.Missing),
            "a verified shortage and relevant audience must persist for three seconds");
        check(!decision.Observe(80, context, MedicalSupplies.Missing), "the same serious condition does not request again after a cooldown");
        context.NearbyTeammate = false; decision.Observe(90, context, MedicalSupplies.Missing);
        context.NearbyTeammate = true;
        check(!decision.Observe(100, context, MedicalSupplies.Missing), "teammate departure and return do not reopen the decision");
        decision.Pause();
        check(!decision.Observe(120, context, MedicalSupplies.Missing), "menu or voice suspension preserves the already decided condition");
        decision.Observe(121, context, MedicalSupplies.Available);
        check(!decision.Observe(130, context, MedicalSupplies.Missing), "brief restocking does not reroll an unresolved injury");
        context.Healing = true; decision.Observe(140, context, MedicalSupplies.Missing);
        context.Healing = false;
        check(!decision.Observe(150, context, MedicalSupplies.Missing), "starting then cancelling treatment does not authorize another request");
        context.Injury = InjuryLevel.Healthy; decision.Observe(160, context, MedicalSupplies.Missing);
        context.Injury = InjuryLevel.BadlyInjured;
        check(!decision.Observe(164, context, MedicalSupplies.Missing), "a transient severity change does not reopen a decided request");
        context.Injury = InjuryLevel.Injured;
        decision.Observe(170, context, MedicalSupplies.Missing); decision.Observe(175, context, MedicalSupplies.Missing);
        context.Injury = InjuryLevel.BadlyInjured;
        check(!decision.Observe(176, context, MedicalSupplies.Missing) && decision.Observe(179, context, MedicalSupplies.Missing),
            "a later serious condition after sustained recovery gets a fresh decision");

        decision = new MedicalRequestDecision();
        decision.Observe(10, context, MedicalSupplies.Missing);
        decision.Observe(12, context, MedicalSupplies.Unknown);
        check(!decision.Observe(13, context, MedicalSupplies.Missing)
            && !decision.Observe(15.9f, context, MedicalSupplies.Missing)
            && decision.Observe(16, context, MedicalSupplies.Missing),
            "uncertain contents interrupt the stability window instead of contributing time");
        decision = new MedicalRequestDecision();
        decision.Observe(10, context, MedicalSupplies.Missing); decision.Pause();
        check(!decision.Observe(30, context, MedicalSupplies.Missing) && decision.Observe(33, context, MedicalSupplies.Missing),
            "an undecided condition must settle again after a pause rather than accumulating menu time");
        decision = new MedicalRequestDecision(); decision.Spoken();
        check(!decision.Observe(20, context, MedicalSupplies.Missing), "manual medical requests satisfy the current condition");
        decision = new MedicalRequestDecision();
        decision.Observe(10, context, MedicalSupplies.Missing); decision.Observe(13, context, MedicalSupplies.Missing);
        check(!decision.Observe(100, context, MedicalSupplies.Missing),
            "missing exact audio and expired pending requests do not grant fallback attempts");

        var policy = new VoicePolicy();
        var request = new VoiceCue { Key = "med:a", Group = "medical-request", Repeat = 60, GroupGap = 60,
            Intent = VoiceIntent.MedicalRequest };
        var hurt = new VoiceCue { Key = "health:a", Group = "health", Urgent = true };
        policy.Spoken(10, 3, 8, true, false, 12, hurt);
        check(!policy.CanUse(request, 13.39f) && policy.CanUse(request, 13.41f),
            "medical requests wait for actual speech but can use the ordinary global gap");
        policy.Manual(21, 12);
        check(!policy.CanUse(request, 32.9f), "medical requests respect manual speech priority");
        policy.Spoken(34, 2, 8, true, false, 12, request);
        check(policy.CanUse(hurt, 37), "a medical request cannot impose its long cooldown on a later urgent injury");
        var anotherTeammate = new VoiceCue { Key = "med:b", Group = "medical-request" };
        check(!policy.CanUse(anotherTeammate, 80), "changing teammates cannot bypass the request category cooldown");
        check(MedicalRequestPolicy.Priority < SemanticVoice.ReloadPriority
            && MedicalRequestPolicy.Priority < SemanticVoice.InjuryPriority(InjuryLevel.BadlyInjured)
            && MedicalRequestPolicy.Priority < SemanticVoice.GrenadeWarningPriority,
            "immediate actions and survival reactions outrank a medical supply request");
    }
}
