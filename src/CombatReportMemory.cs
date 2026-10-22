using System;

namespace ImmersiveVoice
{
    internal enum CombatReportKind
    {
        None, EnemyHit, ScavSpotted, EnemyDown, ScavDown, KnifeKill,
        GoodWork, HoldFire, SniperThreat
    }

    internal struct CombatReport
    {
        public CombatReportKind Kind;
        public string Subject;
        public int Actor;
        public int ActorSlot, ActorVersion;
        public int Version;
        public float OccurredAt;
        public bool Exists => Kind != CombatReportKind.None && (Subject != null || Actor > 0) && Version != 0;
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
                case CombatReportKind.HoldFire: return 42;
                case CombatReportKind.GoodWork: return 66;
                case CombatReportKind.ScavSpotted: return 70; // Scav
                case CombatReportKind.SniperThreat: return 71; // SniperPhrase
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
                case CombatReportKind.HoldFire: return 8;
                case CombatReportKind.SniperThreat: return 6;
                case CombatReportKind.KnifeKill: return 5;
                case CombatReportKind.GoodWork: return 5;
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
        public const int PeerCapacity = 3;
        public const float LivingWitnessWindow = 10f;
        public const float KnifeProofWindow = 1.5f;
        public const float HitTargetRepeat = 12f;
        public const float HitGlobalGap = 4f;
        public const float ScavGlobalGap = 8f;
        public const float RescueWindow = 12f;
        public const float RescueFinishWindow = 3f;
        public const float RecentAttackerWindow = 2f;
        public const float SniperGlobalGap = 10f;
        public const float SniperTargetRepeat = 30f;
        public const float LongRangeShotDistance = 80f;
        public const float FriendlyHitWindow = 1.25f;
        public const float FriendlySignificantDamage = 18f;
        public const float FriendlyRepeatedDamage = 8f;
        public const float HoldFireRepeat = 15f;

        private sealed class Slot
        {
            public string Target;
            public float TouchedAt = -1000f;
            public float SeenAliveAt = -1000f;
            public float LocalKnifeAt = -1000f;
            public float HitReportedAt = -1000f;
            public float LocalHurtAt = -1000f;
            public float RescueHitAt = -1000f;
            public float SniperReportedAt = -1000f;
            public int RescueActor;
            public int RescueSlot = -1, RescuePeerVersion;
            public int Version;
            public bool EnemyScav;
            public bool ScavReported;
            public bool Dead;
        }

        private sealed class PeerSlot
        {
            public int Actor;
            public int IdentityVersion;
            public int Version;
            public int Hits;
            public float TouchedAt = -1000f;
            public float StartedAt = -1000f;
            public float Damage;
            public float ReportedAt = -1000f;
        }

        private readonly Slot[] _slots = { new Slot(), new Slot(), new Slot(), new Slot() };
        private readonly PeerSlot[] _peers = { new PeerSlot(), new PeerSlot(), new PeerSlot() };
        private float _lastHitReport = -1000f;
        private float _lastScavReport = -1000f;
        private float _lastSniperReport = -1000f;
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

        // Called from the local player's actual damage event with its concrete
        // attacker. Rescue evidence is recorded only while that same attacker is
        // visible; a sniper warning may use the existing two-second visual memory.
        public CombatReport ObserveHostileDamage(string target, float now, bool hostile, bool perceived,
            bool attackerConfirmed, float positiveDamage, bool reliableSniperWeapon,
            bool explicitLongRangeShot, float distance)
        {
            if (string.IsNullOrEmpty(target) || !hostile || !attackerConfirmed || !Positive(positiveDamage))
                return default;
            Slot slot = perceived ? Get(target, now) : Find(target);
            if (slot == null || slot.Dead || !perceived && now - slot.SeenAliveAt > RecentAttackerWindow)
                return default;
            slot.TouchedAt = now;
            if (perceived)
            {
                slot.SeenAliveAt = now;
                slot.LocalHurtAt = now;
                slot.RescueActor = 0;
                slot.RescueHitAt = -1000f;
            }
            bool longRange = explicitLongRangeShot && Finite(distance) && distance >= LongRangeShotDistance;
            if ((!reliableSniperWeapon && !longRange) || now - slot.SniperReportedAt < SniperTargetRepeat
                || now - _lastSniperReport < SniperGlobalGap) return default;
            slot.SniperReportedAt = _lastSniperReport = now;
            return Report(slot, CombatReportKind.SniperThreat, now);
        }

        // The teammate must be a currently perceived, same-group human and must
        // deal real positive damage to the exact threat that recently hurt us.
        public void ObserveTeammateHit(string target, int actor, float now, bool hostile,
            bool targetPerceived, bool teammateHuman, bool teammateAlly, bool teammatePerceived,
            float positiveDamage, int actorSlot = -1, int actorVersion = 0)
        {
            if (string.IsNullOrEmpty(target) || actor <= 0 || !hostile || !targetPerceived
                || !teammateHuman || !teammateAlly || !teammatePerceived || !Positive(positiveDamage)) return;
            Slot slot = Find(target);
            if (slot == null || slot.Dead || now < slot.LocalHurtAt || now - slot.LocalHurtAt > RescueWindow) return;
            GetPeer(actor, now, actorVersion).TouchedAt = now;
            slot.TouchedAt = slot.SeenAliveAt = now;
            slot.RescueActor = actor;
            slot.RescueSlot = actorSlot;
            slot.RescuePeerVersion = actorVersion;
            slot.RescueHitAt = now;
        }

        // A command to stop firing needs either a meaningful single friendly hit
        // or repeated real damage in one short burst. Membership/human identity
        // are adapter-provided facts from the existing bounded squad roster.
        public CombatReport ObserveFriendlyDamage(int actor, float now, bool teammateHuman,
            bool teammateAlly, float positiveDamage, int actorSlot = -1, int actorVersion = 0)
        {
            if (actor <= 0 || !teammateHuman || !teammateAlly || !Positive(positiveDamage)) return default;
            PeerSlot peer = GetPeer(actor, now, actorVersion);
            if (now - peer.StartedAt > FriendlyHitWindow)
            {
                peer.StartedAt = now;
                peer.Hits = 0;
                peer.Damage = 0f;
            }
            peer.TouchedAt = now;
            peer.Hits++;
            peer.Damage += positiveDamage;
            bool significant = positiveDamage >= FriendlySignificantDamage;
            bool repeated = peer.Hits >= 2 && peer.Damage >= FriendlyRepeatedDamage;
            if ((!significant && !repeated) || now - peer.ReportedAt < HoldFireRepeat) return default;
            peer.ReportedAt = now;
            peer.Version = NextVersion();
            return new CombatReport { Kind = CombatReportKind.HoldFire, Actor = actor,
                ActorSlot = actorSlot, ActorVersion = actorVersion,
                Version = peer.Version, OccurredAt = now };
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
            bool rescued = slot.RescueActor > 0 && slot.RescueHitAt >= slot.LocalHurtAt
                && now - slot.LocalHurtAt <= RescueWindow && now - slot.RescueHitAt <= RescueFinishWindow;
            CombatReportKind kind = rescued ? CombatReportKind.GoodWork
                : now - slot.LocalKnifeAt <= KnifeProofWindow
                ? CombatReportKind.KnifeKill
                : slot.EnemyScav ? CombatReportKind.ScavDown : CombatReportKind.EnemyDown;
            CombatReport report = Report(slot, kind, now, rescued ? slot.RescueActor : 0);
            if (rescued)
            {
                report.ActorSlot = slot.RescueSlot;
                report.ActorVersion = slot.RescuePeerVersion;
            }
            return report;
        }

        // A queued hit/contact report becomes stale as soon as a more valuable
        // state transition for that same target increments the version.
        public bool Valid(CombatReport report, float now)
        {
            if (!report.Exists) return false;
            if (report.Kind == CombatReportKind.HoldFire)
            {
                PeerSlot friendly = FindPeer(report.Actor);
                return friendly != null && friendly.Version == report.Version && now - report.OccurredAt < 3f;
            }
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
                case CombatReportKind.GoodWork:
                    return slot.Dead && slot.RescueActor == report.Actor && now - report.OccurredAt < 7f;
                case CombatReportKind.SniperThreat:
                    return !slot.Dead && now - report.OccurredAt < 3f
                        && now - slot.SeenAliveAt <= RecentAttackerWindow;
                default:
                    return false;
            }
        }

        public void Clear()
        {
            for (int i = 0; i < Capacity; i++) Reset(_slots[i]);
            for (int i = 0; i < PeerCapacity; i++) Reset(_peers[i]);
            _lastHitReport = _lastScavReport = _lastSniperReport = -1000f;
        }

        public void RemovePeer(int actor)
        {
            PeerSlot peer = FindPeer(actor);
            if (peer != null) Reset(peer);
            ClearRescuer(actor);
        }

        private void ClearRescuer(int actor)
        {
            for (int i = 0; i < Capacity; i++)
                if (_slots[i].RescueActor == actor)
                {
                    _slots[i].RescueActor = 0;
                    _slots[i].RescueSlot = -1;
                    _slots[i].RescuePeerVersion = 0;
                    _slots[i].RescueHitAt = -1000f;
                }
        }

        private CombatReport Report(Slot slot, CombatReportKind kind, float now, int actor = 0)
        {
            slot.Version = NextVersion();
            return new CombatReport { Kind = kind, Subject = slot.Target, Actor = actor,
                Version = slot.Version, OccurredAt = now };
        }

        private int NextVersion()
        {
            int version = ++_nextVersion;
            return version == 0 ? ++_nextVersion : version;
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

        private PeerSlot FindPeer(int actor)
        {
            for (int i = 0; i < PeerCapacity; i++)
                if (_peers[i].Actor == actor) return _peers[i];
            return null;
        }

        private PeerSlot GetPeer(int actor, float now, int identityVersion = 0)
        {
            PeerSlot existing = FindPeer(actor);
            if (existing != null)
            {
                if (existing.IdentityVersion == identityVersion) return existing;
                ClearRescuer(actor);
                Reset(existing);
                existing.Actor = actor;
                existing.IdentityVersion = identityVersion;
                existing.TouchedAt = now;
                return existing;
            }
            int selected = 0;
            for (int i = 1; i < PeerCapacity; i++)
                if (_peers[i].Actor == 0 || _peers[i].TouchedAt < _peers[selected].TouchedAt) selected = i;
            PeerSlot peer = _peers[selected];
            int evicted = peer.Actor;
            if (evicted > 0) ClearRescuer(evicted);
            Reset(peer);
            peer.Actor = actor;
            peer.IdentityVersion = identityVersion;
            peer.TouchedAt = now;
            return peer;
        }

        private static bool Positive(float value) => Finite(value) && value > 0f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static void Reset(Slot slot)
        {
            slot.Target = null;
            slot.TouchedAt = slot.SeenAliveAt = slot.LocalKnifeAt = slot.HitReportedAt = -1000f;
            slot.LocalHurtAt = slot.RescueHitAt = slot.SniperReportedAt = -1000f;
            slot.Version = 0;
            slot.RescueActor = 0;
            slot.RescueSlot = -1;
            slot.RescuePeerVersion = 0;
            slot.EnemyScav = slot.ScavReported = slot.Dead = false;
        }

        private static void Reset(PeerSlot peer)
        {
            peer.Actor = peer.IdentityVersion = peer.Version = peer.Hits = 0;
            peer.TouchedAt = peer.StartedAt = peer.ReportedAt = -1000f;
            peer.Damage = 0f;
        }
    }
}
