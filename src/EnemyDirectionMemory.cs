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

namespace ImmersiveVoice
{
    internal enum TacticalStageKind { None, Silence, EnemyConversation }

    internal static class TacticalStageRules
    {
        public static int Phrase(TacticalStageKind kind)
            => kind == TacticalStageKind.EnemyConversation ? 17
                : kind == TacticalStageKind.Silence ? 39 : 0;

        // Clear cannot be inferred from a failed sight probe; Look has no exact
        // observable object/recipient contract; LostVisual is already represented
        // by the distinct existing OnLostVisual event. None receives a substitute.
        public static bool SupportedPhrase(int phrase) => phrase == 17 || phrase == 39;
    }

    internal struct StealthApproachDecision
    {
        public int Peer, PeerVersion, ThreatVersion, Actor;
        public string Threat;
        public float SelectedAt;
        public bool Exists => Peer >= 0 && Threat != null && ThreatVersion != 0;
    }

    // Consumes the director's existing quarter-second local/teammate samples and
    // visible-threat observations. Three peer slots plus the local human are the
    // entire four-player squad; no physics, roster scan, LINQ, or network work.
    internal sealed class StealthApproachMemory
    {
        public const int PeerCapacity = 3, ThreatCapacity = 4;
        public const float ThreatFreshness = 1f, NormalTransition = 0.5f, StealthDuration = 1.5f;
        private const float NormalSpeed = 0.6f, StealthSpeed = 0.1f, LayerTolerance = 2.5f;

        private sealed class ThreatSlot
        {
            public string Id;
            public SquadPoint Position;
            public float SeenAt = -1000f;
            public bool Alive;
        }
        private sealed class PeerSlot
        {
            public int Version;
            public float SampleAt = -1000f, NormalSince = -1f, StealthSince = -1f;
            public SquadPoint Self, Other;
            public bool HadNormal, Stealthing, Ready;
        }

        private readonly ThreatSlot[] _threats = { new ThreatSlot(), new ThreatSlot(), new ThreatSlot(), new ThreatSlot() };
        private readonly PeerSlot[] _peers = { new PeerSlot(), new PeerSlot(), new PeerSlot() };
        private string _activeThreat;
        private int _threatVersion;
        private bool _selected, _combat;

        public void Threat(string id, SquadPoint position, bool alive, bool focused, float now)
        {
            if (string.IsNullOrEmpty(id) || !position.Finite) return;
            ThreatSlot slot = GetThreat(id);
            slot.Position = position;
            slot.SeenAt = now;
            slot.Alive = alive;
            if (!alive)
            {
                if (_activeThreat == id) CancelThreat();
                return;
            }
            if (!focused || _activeThreat == id) return;
            _activeThreat = id;
            _threatVersion++;
            if (_threatVersion == 0) _threatVersion++;
            _selected = false;
            ResetPeers();
        }

        public void Lost(string id)
        {
            if (!string.IsNullOrEmpty(id) && _activeThreat == id) CancelThreat();
        }

        public void Sample(int peer, float now, SquadPoint self, SquadPoint other, bool perceivedHuman,
            bool sameLayer, bool selfStealth, bool otherStealth, bool combat)
        {
            if ((uint)peer >= PeerCapacity) return;
            _combat = combat;
            PeerSlot state = _peers[peer];
            ThreatSlot threat = CurrentThreat(now);
            if (combat || threat == null || !perceivedHuman || !sameLayer || !self.Finite || !other.Finite
                || Math.Abs(self.Y - other.Y) > LayerTolerance)
            {
                Reset(state);
                if (combat || threat == null) CancelThreat();
                return;
            }

            float dt = now - state.SampleAt;
            if (state.SampleAt < -900f || dt <= 0f || dt > 1f)
            {
                Reset(state);
                state.SampleAt = now;
                state.Self = self;
                state.Other = other;
                return;
            }

            bool selfNormal = !selfStealth && MovingToward(state.Self, self, threat.Position, dt, NormalSpeed);
            bool otherNormal = !otherStealth && MovingToward(state.Other, other, threat.Position, dt, NormalSpeed);
            bool selfQuiet = selfStealth && MovingToward(state.Self, self, threat.Position, dt, StealthSpeed);
            bool otherQuiet = otherStealth && MovingToward(state.Other, other, threat.Position, dt, StealthSpeed);
            if (selfNormal && otherNormal)
            {
                if (state.NormalSince < 0f) state.NormalSince = now;
                if (now - state.NormalSince + 0.0001f >= NormalTransition) state.HadNormal = true;
                state.StealthSince = -1f;
                state.Stealthing = state.Ready = false;
            }
            else if (state.HadNormal && selfQuiet && otherQuiet)
            {
                if (state.StealthSince < 0f) state.StealthSince = now;
                state.Stealthing = true;
                state.Ready = now - state.StealthSince + 0.0001f >= StealthDuration;
            }
            else
            {
                state.NormalSince = -1f;
                state.StealthSince = -1f;
                state.Stealthing = state.Ready = false;
                if (!selfQuiet || !otherQuiet) state.HadNormal = false;
            }
            state.SampleAt = now;
            state.Self = self;
            state.Other = other;
        }

        public StealthApproachDecision Select(float now)
        {
            if (_selected || _combat || CurrentThreat(now) == null) return default;
            for (int i = 0; i < PeerCapacity; i++)
            {
                PeerSlot peer = _peers[i];
                if (!peer.Ready || now - peer.SampleAt > 0.4f) continue;
                _selected = true;
                peer.Ready = false;
                return new StealthApproachDecision
                {
                    Peer = i,
                    PeerVersion = peer.Version,
                    Threat = _activeThreat,
                    ThreatVersion = _threatVersion,
                    SelectedAt = now
                };
            }
            return default;
        }

        public bool Valid(StealthApproachDecision decision, float now)
        {
            if (!decision.Exists || _combat || decision.Peer < 0 || decision.Peer >= PeerCapacity
                || decision.ThreatVersion != _threatVersion || decision.Threat != _activeThreat
                || now - decision.SelectedAt > 2f || CurrentThreat(now) == null) return false;
            PeerSlot peer = _peers[decision.Peer];
            return peer.Version == decision.PeerVersion && peer.Stealthing && now - peer.SampleAt <= 0.4f;
        }

        public void Clear()
        {
            for (int i = 0; i < ThreatCapacity; i++)
            {
                _threats[i].Id = null;
                _threats[i].SeenAt = -1000f;
                _threats[i].Alive = false;
            }
            _activeThreat = null;
            _threatVersion++;
            _selected = _combat = false;
            ResetPeers();
        }

        public void ResetPeer(int peer)
        {
            if ((uint)peer >= PeerCapacity) return;
            PeerSlot state = _peers[peer];
            if (state.SampleAt > -900f || state.HadNormal || state.Stealthing || state.Ready) Reset(state);
        }

        private ThreatSlot CurrentThreat(float now)
        {
            if (_activeThreat == null) return null;
            for (int i = 0; i < ThreatCapacity; i++)
            {
                ThreatSlot slot = _threats[i];
                if (slot.Id == _activeThreat && slot.Alive && now - slot.SeenAt <= ThreatFreshness) return slot;
            }
            return null;
        }

        private ThreatSlot GetThreat(string id)
        {
            int selected = 0;
            for (int i = 0; i < ThreatCapacity; i++)
            {
                if (_threats[i].Id == id) return _threats[i];
                if (_threats[i].Id == null || _threats[i].SeenAt < _threats[selected].SeenAt) selected = i;
            }
            ThreatSlot slot = _threats[selected];
            if (slot.Id == _activeThreat) CancelThreat();
            slot.Id = id;
            slot.SeenAt = -1000f;
            slot.Alive = false;
            return slot;
        }

        private static bool MovingToward(SquadPoint before, SquadPoint current, SquadPoint target, float dt, float speed)
        {
            SquadPoint move = current - before;
            float minimum = speed * dt;
            if (move.FlatSquared < minimum * minimum || move.FlatSquared > 100f) return false;
            SquadPoint toward = target - before;
            return SquadPoint.Dot(move, toward) > 0f && (target - current).FlatSquared < toward.FlatSquared;
        }

        private void CancelThreat()
        {
            if (_activeThreat == null) return;
            _activeThreat = null;
            _threatVersion++;
            _selected = false;
            ResetPeers();
        }
        private void ResetPeers()
        {
            for (int i = 0; i < PeerCapacity; i++) Reset(_peers[i]);
        }
        private static void Reset(PeerSlot peer)
        {
            peer.Version++;
            peer.SampleAt = -1000f;
            peer.NormalSince = peer.StealthSince = -1f;
            peer.HadNormal = peer.Stealthing = peer.Ready = false;
        }
    }

    internal struct EnemyConversationDecision
    {
        public string Npc;
        public int Round, Version;
        public float HeardAt;
        public bool Exists => Npc != null && Round != 0 && Version != 0;
    }

    // Created only from a verified native speaker callback after the existing
    // audibility probe classifies that concrete AI speaker as hostile.
    internal sealed class EnemyConversationMemory
    {
        public const int Capacity = 4;
        private sealed class Slot
        {
            public string Npc;
            public int Version;
            public float HeardAt = -1000f;
        }
        private readonly Slot[] _slots = { new Slot(), new Slot(), new Slot(), new Slot() };
        private int _round, _version;

        public EnemyConversationDecision Hear(string npc, int round, float now, bool realVoice,
            bool audible, bool hostileNpc, bool conversational)
        {
            if (round <= 0 || string.IsNullOrEmpty(npc) || !realVoice || !audible || !hostileNpc || !conversational)
                return default;
            if (round != _round) BeginRound(round);
            for (int i = 0; i < Capacity; i++)
                if (_slots[i].Npc == npc) return default;

            int selected = -1;
            for (int i = 0; i < Capacity; i++)
                if (_slots[i].Npc == null) { selected = i; break; }
            if (selected < 0) return default; // preserve per-NPC raid deduplication when bounded storage is full
            Slot slot = _slots[selected];
            slot.Npc = npc;
            slot.HeardAt = now;
            slot.Version = ++_version;
            if (slot.Version == 0) slot.Version = ++_version;
            return new EnemyConversationDecision { Npc = npc, Round = round, Version = slot.Version, HeardAt = now };
        }

        public bool Valid(EnemyConversationDecision decision, float now)
        {
            if (!decision.Exists || decision.Round != _round || now - decision.HeardAt > 5f) return false;
            for (int i = 0; i < Capacity; i++)
                if (_slots[i].Npc == decision.Npc) return _slots[i].Version == decision.Version;
            return false;
        }

        public void BeginRound(int round)
        {
            _round = round;
            for (int i = 0; i < Capacity; i++)
            {
                _slots[i].Npc = null;
                _slots[i].Version = 0;
                _slots[i].HeardAt = -1000f;
            }
        }

        public void Clear() => BeginRound(0);
    }
}
