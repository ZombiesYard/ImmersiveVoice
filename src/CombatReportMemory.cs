using System;

namespace ImmersiveVoice
{
    internal enum CombatReportKind
    {
        None, EnemyHit, ScavSpotted, EnemyDown, ScavDown, KnifeKill
    }

    internal struct CombatReport
    {
        public CombatReportKind Kind;
        public string Subject;
        public int Version;
        public float OccurredAt;
        public bool Exists => Kind != CombatReportKind.None && Subject != null && Version != 0;
    }

    internal static class CombatReportRules
    {
        // Verified native EPhraseTrigger values. A missing exact bank remains
        // silent; callers must never ask this method for a fallback meaning.
        public static int ExactPhrase(CombatReportKind kind, bool exactBankExists)
        {
            if (!exactBankExists) return 0;
            switch (kind)
            {
                case CombatReportKind.ScavSpotted: return 70; // Scav
                case CombatReportKind.EnemyDown: return 75;
                case CombatReportKind.ScavDown: return 76;
                case CombatReportKind.EnemyHit: return 78;
                case CombatReportKind.KnifeKill: return 79;
                default: return 0;
            }
        }

        public static int Priority(CombatReportKind kind)
        {
            switch (kind)
            {
                case CombatReportKind.KnifeKill: return 5;
                case CombatReportKind.ScavDown: return 4;
                case CombatReportKind.EnemyDown: return 3;
                case CombatReportKind.ScavSpotted: return 2;
                case CombatReportKind.EnemyHit: return 1;
                default: return 0;
            }
        }
    }

    // Four locally perceived targets are enough for a four-human squad without
    // consulting a raid roster. All inputs are facts already produced by the
    // existing hit callback and budgeted visual probe; this class performs no
    // polling, physics work, LINQ, or allocation on its hot path.
    internal sealed class CombatReportMemory
    {
        public const int Capacity = 4;
        public const float LivingWitnessWindow = 10f;
        public const float KnifeProofWindow = 1.5f;
        public const float HitTargetRepeat = 12f;
        public const float HitGlobalGap = 4f;
        public const float ScavGlobalGap = 8f;

        private sealed class Slot
        {
            public string Target;
            public float TouchedAt = -1000f;
            public float SeenAliveAt = -1000f;
            public float LocalKnifeAt = -1000f;
            public float HitReportedAt = -1000f;
            public int Version;
            public bool EnemyScav;
            public bool ScavReported;
            public bool Dead;
        }

        private readonly Slot[] _slots = { new Slot(), new Slot(), new Slot(), new Slot() };
        private float _lastHitReport = -1000f;
        private float _lastScavReport = -1000f;
        private int _nextVersion;

        // "enemyScav" must come from classification of this actually perceived
        // target. It must not be inferred from a remote kill feed or an unseen AI.
        public CombatReport ObserveLiving(string target, float now, bool hostile, bool perceived, bool enemyScav,
            bool reportable = true)
        {
            if (string.IsNullOrEmpty(target) || !hostile || !perceived) return default;
            Slot slot = Get(target, now);
            if (slot.Dead) return default;
            slot.SeenAliveAt = slot.TouchedAt = now;
            slot.EnemyScav |= enemyScav;
            if (!reportable || !slot.EnemyScav || slot.ScavReported || now - _lastScavReport < ScavGlobalGap)
                return default;
            slot.ScavReported = true;
            _lastScavReport = now;
            return Report(slot, CombatReportKind.ScavSpotted, now);
        }

        // The local-attacker and positive-damage checks reject replicated hits,
        // armor/no-damage hints, and native OnEnemyShot HUD information. The
        // target must also be locally perceived at the time of the real callback.
        public CombatReport ObserveHit(string target, float now, bool hostile, bool perceived,
            bool localAttacker, bool positiveDamage, bool knife, bool enemyScav)
        {
            if (string.IsNullOrEmpty(target) || !hostile || !perceived || !localAttacker || !positiveDamage)
                return default;
            Slot slot = Get(target, now);
            if (slot.Dead) return default;
            slot.SeenAliveAt = slot.TouchedAt = now; // An actual impact is living evidence.
            slot.EnemyScav |= enemyScav;
            // The adapter must require the concrete native Knife weapon, not
            // merely a broad melee/blunt damage label.
            if (knife) slot.LocalKnifeAt = now;

            // First identify a concrete Scav rather than also announcing the hit
            // from the same callback. The exact target report is the richer fact.
            if (slot.EnemyScav && !slot.ScavReported && now - _lastScavReport >= ScavGlobalGap)
            {
                slot.ScavReported = true;
                _lastScavReport = now;
                return Report(slot, CombatReportKind.ScavSpotted, now);
            }
            if (now - slot.HitReportedAt < HitTargetRepeat || now - _lastHitReport < HitGlobalGap)
                return default;
            slot.HitReportedAt = _lastHitReport = now;
            return Report(slot, CombatReportKind.EnemyHit, now);
        }

        // Death is authorized only after an actual local visual confirmation and
        // a recent living witness. A melee line additionally requires this local
        // player to have dealt a real melee impact to this exact target.
        public CombatReport ObserveDeath(string target, float now, bool hostile, bool perceived,
            bool deathConfirmed, bool enemyScav)
        {
            if (string.IsNullOrEmpty(target) || !hostile || !perceived || !deathConfirmed) return default;
            Slot slot = Find(target);
            if (slot == null || slot.Dead || now - slot.SeenAliveAt > LivingWitnessWindow) return default;
            slot.TouchedAt = now;
            slot.EnemyScav |= enemyScav;
            slot.Dead = true;
            CombatReportKind kind = now - slot.LocalKnifeAt <= KnifeProofWindow
                ? CombatReportKind.KnifeKill
                : slot.EnemyScav ? CombatReportKind.ScavDown : CombatReportKind.EnemyDown;
            return Report(slot, kind, now);
        }

        // A queued hit/contact report becomes stale as soon as a more valuable
        // state transition for that same target increments the version.
        public bool Valid(CombatReport report, float now)
        {
            if (!report.Exists) return false;
            Slot slot = Find(report.Subject);
            if (slot == null || slot.Version != report.Version) return false;
            switch (report.Kind)
            {
                case CombatReportKind.EnemyHit:
                    return !slot.Dead && now - report.OccurredAt < 2.5f && now - slot.SeenAliveAt < 2.5f;
                case CombatReportKind.ScavSpotted:
                    return !slot.Dead && now - report.OccurredAt < 3f && now - slot.SeenAliveAt < 3f;
                case CombatReportKind.EnemyDown:
                case CombatReportKind.ScavDown:
                case CombatReportKind.KnifeKill:
                    return slot.Dead && now - report.OccurredAt < 7f;
                default:
                    return false;
            }
        }

        public void Clear()
        {
            for (int i = 0; i < Capacity; i++) Reset(_slots[i]);
            _lastHitReport = _lastScavReport = -1000f;
        }

        private CombatReport Report(Slot slot, CombatReportKind kind, float now)
        {
            slot.Version = ++_nextVersion;
            if (slot.Version == 0) slot.Version = ++_nextVersion;
            return new CombatReport { Kind = kind, Subject = slot.Target, Version = slot.Version, OccurredAt = now };
        }

        private Slot Find(string target)
        {
            for (int i = 0; i < Capacity; i++)
                if (string.Equals(_slots[i].Target, target, StringComparison.Ordinal)) return _slots[i];
            return null;
        }

        private Slot Get(string target, float now)
        {
            Slot existing = Find(target);
            if (existing != null) return existing;
            int selected = 0;
            for (int i = 1; i < Capacity; i++)
                if (_slots[i].Target == null || _slots[i].TouchedAt < _slots[selected].TouchedAt) selected = i;
            Slot slot = _slots[selected];
            Reset(slot);
            slot.Target = target;
            slot.TouchedAt = now;
            return slot;
        }

        private static void Reset(Slot slot)
        {
            slot.Target = null;
            slot.TouchedAt = slot.SeenAliveAt = slot.LocalKnifeAt = slot.HitReportedAt = -1000f;
            slot.Version = 0;
            slot.EnemyScav = slot.ScavReported = slot.Dead = false;
        }
    }
}
