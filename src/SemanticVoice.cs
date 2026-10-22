namespace ImmersiveVoice
{
    // Meanings owned by the mod; EFT trigger names belong in the adapters.
    internal enum VoiceIntent
    {
        None, Silent, ReloadAction, ReloadTactical, EmptyWeapon, NeedAmmo,
        Malfunction, GrenadeOut, GrenadeWarning, Injury, FriendlyFire, Healing,
        Contact, LostVisual, KillConfirm, TacticalEnemyDown, KillTaunt,
        Recovery, Acknowledge, CombatBark, CalmMutter, NativeContext,
        RepeatedContact, Bleeding, ArmFracture, LegFracture, MedicalRequest, TeamFollowing, TeamCovering, TeamAdvance, TeamRetreat,
        TeamLead, TeamNeedCover, TeamRegroup, TeamHold, FriendlyDown, TeamOnPosition, TeamGoodWork, NpcRetort, NpcNoise, NpcMutter, NpcAcknowledge, NpcFollowing, NpcCovering, TeamReady
    }

    internal enum VoiceMode { Solo, FikaHost, FikaClient }
    internal enum InjuryLevel { Healthy, Injured, BadlyInjured, Dying }
    internal enum CombatPhase { Calm, Engaged, Recovering }

    // A snapshot of existing director state, not a second source of context.
    internal struct BarkContext
    {
        public VoiceMode Mode;
        public CombatPhase Combat;
        public InjuryLevel Injury;
        public bool NearbyTeammate, RecentTeammate, Healing, RecentlyHit;
        public bool Bleeding, HeavyBleeding, ArmFracture, LegFracture;
        public float FightDuration, PeakPressure;
        public bool TookHostileHit, HadMalfunction, EndingSelected;
        public int RecentObservedDowns;
        public int DamageVersion;
        public bool TeamInformation => Mode != VoiceMode.Solo && NearbyTeammate;
        public bool TeamReport => Mode != VoiceMode.Solo && (NearbyTeammate || RecentTeammate);
        public bool Wounded => Injury != InjuryLevel.Healthy || Bleeding || HeavyBleeding || ArmFracture || LegFracture;
        public bool Survival => Injury >= InjuryLevel.BadlyInjured || HeavyBleeding || Healing || RecentlyHit;
    }

    internal static class SemanticVoice
    {
        public const float RecentHitWindow = 2f;
        public const int TauntPriority = 40;
        public const int ReloadPriority = 97, GrenadeOutPriority = 100, GrenadeWarningPriority = 105;
        public const float DefaultCalmReloadChance = 50f, DefaultRecoveryReloadChance = 65f;

        // Preserve combat reloads; reduce routine top-ups outside an exchange.
        // One roll belongs to the action, never to each native callback/tick.
        public static float ReloadChance(BarkContext context, float calmPercent = DefaultCalmReloadChance,
            float recoveryPercent = DefaultRecoveryReloadChance)
        {
            if (context.Combat == CombatPhase.Engaged) return 100f;
            float chance = context.Combat == CombatPhase.Calm ? calmPercent : recoveryPercent;
            chance = System.Math.Max(0f, System.Math.Min(100f, chance));
            return context.Combat == CombatPhase.Recovering && context.PeakPressure >= 4f
                ? System.Math.Max(75f, chance) : chance;
        }
        public static bool Reload(BarkContext context, double roll, float calmPercent = DefaultCalmReloadChance,
            float recoveryPercent = DefaultRecoveryReloadChance)
            => context.Combat == CombatPhase.Engaged || roll < ReloadChance(context, calmPercent, recoveryPercent) / 100d;
        public static int ReloadPriorityFor(BarkContext context)
            => context.Combat == CombatPhase.Engaged ? ReloadPriority : 65;

        public static VoiceIntent Injury(BarkContext context, bool friendly)
        {
            if (context.Injury == InjuryLevel.Dying) return VoiceIntent.Injury;
            if (context.HeavyBleeding) return VoiceIntent.Bleeding;
            if (context.Injury == InjuryLevel.BadlyInjured) return VoiceIntent.Injury;
            if (context.Bleeding) return VoiceIntent.Bleeding;
            if (context.LegFracture) return VoiceIntent.LegFracture;
            if (context.ArmFracture) return VoiceIntent.ArmFracture;
            return friendly ? VoiceIntent.FriendlyFire : VoiceIntent.Injury;
        }

        public static int InjuryPriority(InjuryLevel injury, bool heavyBleeding = false)
            => injury == InjuryLevel.Dying ? 102 : injury == InjuryLevel.BadlyInjured || heavyBleeding ? 98 : 87;

        private static bool MeaningfulExchange(BarkContext context)
            => context.PeakPressure >= 4f || context.FightDuration >= 8f || context.TookHostileHit
                || context.HadMalfunction || context.RecentObservedDowns >= 2;

        public static bool Recovery(BarkContext context, bool waiting = false)
            => context.Combat == CombatPhase.Recovering && !context.Healing && (waiting || !context.RecentlyHit)
                && (context.Wounded || (!context.EndingSelected && MeaningfulExchange(context)));
        public static bool AfterTreatment(BarkContext context, bool medicalRequestPending = false)
            => !medicalRequestPending && context.Combat != CombatPhase.Engaged && !context.Healing && !context.RecentlyHit
                && (context.Injury >= InjuryLevel.BadlyInjured || context.Bleeding || context.HeavyBleeding
                    || context.ArmFracture || context.LegFracture);
        public static VoiceIntent EnemyDown(BarkContext context, bool tauntRoll)
        {
            if (context.Injury >= InjuryLevel.BadlyInjured || context.HeavyBleeding || context.Healing)
                return VoiceIntent.Silent;
            // A witnessed team report may wait out the existing hit window.
            // Selection does not grant permission to speak over the injury.
            if (context.TeamReport) return VoiceIntent.TacticalEnemyDown;
            if (context.RecentlyHit) return VoiceIntent.Silent;
            if (context.Combat != CombatPhase.Recovering || context.Wounded || !MeaningfulExchange(context))
                return VoiceIntent.Silent;
            return tauntRoll ? VoiceIntent.KillTaunt : VoiceIntent.KillConfirm;
        }

        public static bool StillValid(VoiceIntent intent, BarkContext context, int damageVersion)
            => RejectionReason(intent, context, damageVersion) == null;

        public static string RejectionReason(VoiceIntent intent, BarkContext context, int damageVersion)
            => RejectionReasonCore(intent, context, damageVersion, false);

        public static string PendingRejectionReason(VoiceIntent intent, BarkContext context, int damageVersion)
            => RejectionReasonCore(intent, context, damageVersion, true);

        public static string WaitingReason(VoiceIntent intent, bool recentlyHit)
            => intent == VoiceIntent.TacticalEnemyDown && recentlyHit ? "RecentDamage" : null;

        private static string RejectionReasonCore(VoiceIntent intent, BarkContext context, int damageVersion, bool pending)
        {
            if (intent == VoiceIntent.Silent) return "SilentIntent";
            if (intent == VoiceIntent.TacticalEnemyDown && context.DamageVersion != damageVersion)
                return "DamageAfterRequest";
            bool emotional = intent == VoiceIntent.KillTaunt || intent == VoiceIntent.CombatBark || NpcExchange(intent);
            if (emotional || intent == VoiceIntent.KillConfirm || intent == VoiceIntent.TacticalEnemyDown)
            {
                if (context.Injury >= InjuryLevel.BadlyInjured) return "SevereInjury";
                if (context.HeavyBleeding) return "HeavyBleeding";
                if (context.Healing) return "Healing";
                if (context.RecentlyHit && !(pending && intent == VoiceIntent.TacticalEnemyDown)) return "RecentDamage";
            }
            if (intent == VoiceIntent.KillTaunt || intent == VoiceIntent.KillConfirm)
            {
                if (context.DamageVersion != damageVersion) return "DamageAfterRequest";
                if (context.TeamReport) return "TeamInformationPreferred";
                if (EnemyDown(context, intent == VoiceIntent.KillTaunt) != intent) return "EndingContextChanged";
            }
            if (intent == VoiceIntent.TacticalEnemyDown && !context.TeamReport) return "TeammateNoLongerNearby";
            return null;
        }

        public static bool NpcExchange(VoiceIntent intent) => intent == VoiceIntent.NpcRetort || intent == VoiceIntent.NpcNoise
            || intent == VoiceIntent.NpcMutter || intent == VoiceIntent.NpcAcknowledge || intent == VoiceIntent.NpcFollowing || intent == VoiceIntent.NpcCovering;

        public static VoiceIntent HistoryKey(VoiceIntent intent)
            => intent == VoiceIntent.KillTaunt || intent == VoiceIntent.NpcRetort ? VoiceIntent.CombatBark
                : intent == VoiceIntent.NpcMutter ? VoiceIntent.CalmMutter
                : intent == VoiceIntent.NpcAcknowledge ? VoiceIntent.Acknowledge
                : intent == VoiceIntent.NpcFollowing ? VoiceIntent.TeamFollowing
                : intent == VoiceIntent.NpcCovering ? VoiceIntent.TeamCovering
                : intent == VoiceIntent.RepeatedContact ? VoiceIntent.Contact : intent;
    }
}
