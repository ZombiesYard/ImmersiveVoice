using System;

namespace ImmersiveVoice
{
    internal enum EnemyDirection { None, Front, Rear, Left, Right }

    internal struct EnemyDirectionDecision
    {
        public string Target;
        public int TargetVersion, Observation;
        public EnemyDirection Direction;
        public float SelectedAt;
        public bool Exists => Direction != EnemyDirection.None;
    }

    // Four fixed target slots mirror TargetTracker's local ray-confirmed cache.
    // Direction work is scalar-only: callers supply an already visible hostile,
    // so this model never scans a roster, casts a ray, or allocates in its hot path.
    internal sealed class EnemyDirectionMemory
    {
        public const int Capacity = 4;
        public const float StableTime = 0.2f, FreshTime = 0.8f, ObservationGap = 1.05f;
        public const float GlobalRepeat = 8f, TargetRepeat = 20f;
        private const float MinimumVectorSquared = 0.25f;
        // tan(40 degrees) and tan(50 degrees) leave a five-degree dead band
        // around the ambiguous 45/135 degree category boundaries.
        private const float AxialRatio = 0.8390996f, FlankRatio = 1.1917536f;

        private sealed class Slot
        {
            public string Target;
            public int Version, Observation;
            public float SeenAt = -1000f, DirectionSince = -1000f, ReportedAt = -1000f;
            public EnemyDirection Direction;
            public bool Selected;
        }

        private readonly Slot[] _slots = { new Slot(), new Slot(), new Slot(), new Slot() };
        private float _reportedAt = -1000f;

        public static int Phrase(EnemyDirection direction)
        {
            switch (direction)
            {
                case EnemyDirection.Left: return 69;  // LeftFlank
                case EnemyDirection.Right: return 72; // RightFlank
                case EnemyDirection.Front: return 73; // InTheFront
                case EnemyDirection.Rear: return 74;  // OnSix
                default: return 0;
            }
        }

        public static EnemyDirection Classify(SquadPoint speaker, SquadPoint facing, SquadPoint target)
        {
            if (!speaker.Finite || !facing.Finite || !target.Finite) return EnemyDirection.None;
            float dx = target.X - speaker.X, dz = target.Z - speaker.Z;
            float targetSquared = dx * dx + dz * dz, facingSquared = facing.X * facing.X + facing.Z * facing.Z;
            if (targetSquared < MinimumVectorSquared || facingSquared < 0.01f) return EnemyDirection.None;

            float dot = facing.X * dx + facing.Z * dz;
            float cross = facing.X * dz - facing.Z * dx;
            float side = Math.Abs(cross), axis = Math.Abs(dot);
            if (side <= axis * AxialRatio) return dot >= 0f ? EnemyDirection.Front : EnemyDirection.Rear;
            if (side >= axis * FlankRatio) return cross > 0f ? EnemyDirection.Left : EnemyDirection.Right;
            return EnemyDirection.None;
        }

        public void Observe(string target, SquadPoint speaker, SquadPoint facing, SquadPoint position, float now,
            bool visibleHostile)
        {
            if (string.IsNullOrEmpty(target)) return;
            Slot slot = Find(target, visibleHostile);
            if (slot == null) return;
            if (!visibleHostile)
            {
                slot.SeenAt = -1000f;
                slot.Direction = EnemyDirection.None;
                slot.DirectionSince = -1000f;
                return;
            }

            bool newObservation = now - slot.SeenAt > ObservationGap;
            if (newObservation)
            {
                slot.Observation++;
                slot.Selected = false;
                slot.Direction = EnemyDirection.None;
                slot.DirectionSince = now;
            }
            slot.SeenAt = now;
            EnemyDirection direction = Classify(speaker, facing, position);
            if (direction == slot.Direction) return;
            slot.Version++;
            slot.Direction = direction;
            slot.DirectionSince = now;
        }

        // Selection consumes this sighting even when policy or an absent exact
        // bank later suppresses playback. It cannot cascade to another direction.
        public EnemyDirectionDecision Select(string target, float now)
        {
            Slot slot = Lookup(target);
            if (slot == null || slot.Selected || now - slot.SeenAt > FreshTime
                || slot.Direction == EnemyDirection.None || now - slot.DirectionSince + 0.0001f < StableTime) return default;
            slot.Selected = true;
            if (now - _reportedAt < GlobalRepeat || now - slot.ReportedAt < TargetRepeat) return default;
            slot.ReportedAt = _reportedAt = now;
            return new EnemyDirectionDecision
            {
                Target = slot.Target,
                TargetVersion = slot.Version,
                Observation = slot.Observation,
                Direction = slot.Direction,
                SelectedAt = now
            };
        }

        public bool Valid(EnemyDirectionDecision decision, float now)
        {
            if (!decision.Exists || now - decision.SelectedAt > FreshTime) return false;
            Slot slot = Lookup(decision.Target);
            return slot != null && slot.Version == decision.TargetVersion && slot.Observation == decision.Observation
                && slot.Direction == decision.Direction && now - slot.SeenAt <= FreshTime;
        }

        public void Clear()
        {
            for (int i = 0; i < Capacity; i++)
            {
                Slot slot = _slots[i];
                slot.Target = null;
                slot.Version++;
                slot.Observation = 0;
                slot.SeenAt = slot.DirectionSince = slot.ReportedAt = -1000f;
                slot.Direction = EnemyDirection.None;
                slot.Selected = false;
            }
            _reportedAt = -1000f;
        }

        private Slot Lookup(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            for (int i = 0; i < Capacity; i++)
                if (string.Equals(_slots[i].Target, target, StringComparison.Ordinal)) return _slots[i];
            return null;
        }

        private Slot Find(string target, bool create)
        {
            Slot empty = null, oldest = null;
            float oldestAt = float.MaxValue;
            for (int i = 0; i < Capacity; i++)
            {
                Slot slot = _slots[i];
                if (string.Equals(slot.Target, target, StringComparison.Ordinal)) return slot;
                if (slot.Target == null) empty = slot;
                else if (slot.SeenAt < oldestAt) { oldest = slot; oldestAt = slot.SeenAt; }
            }
            if (empty != null) oldest = empty;
            if (!create || oldest == null) return null;
            oldest.Target = target;
            oldest.Version++;
            oldest.Observation = 0;
            oldest.SeenAt = oldest.DirectionSince = oldest.ReportedAt = -1000f;
            oldest.Direction = EnemyDirection.None;
            oldest.Selected = false;
            return oldest;
        }
    }
}
