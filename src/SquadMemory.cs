using System;

namespace ImmersiveVoice
{
    internal struct SquadPoint
    {
        public float X, Y, Z;
        public SquadPoint(float x, float y, float z) { X = x; Y = y; Z = z; }
        public bool Finite => !float.IsNaN(X + Y + Z) && !float.IsInfinity(X + Y + Z);
        public static SquadPoint operator -(SquadPoint a, SquadPoint b) => new SquadPoint(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public float LengthSquared => X * X + Y * Y + Z * Z;
        public float FlatSquared => X * X + Z * Z;
        public static float Dot(SquadPoint a, SquadPoint b) => a.X * b.X + a.Z * b.Z;
    }

    // No catch-up work or stored credits after a stalled frame.
    internal struct SquadGate
    {
        private float _next;
        public bool Take(float now, float interval)
        {
            if (now < _next) return false;
            _next = now + interval;
            return true;
        }
    }

    internal enum SquadCommand { None, Follow, Cover, Advance, Retreat, Regroup, Hold, Praise }
    internal enum SquadAction { None, Following, Covering, Advancing, Retreating, Regrouping, Holding,
        Leading, RequestCover, Rallying, HoldTeam, Compliment, PushNow, Suppressing, SpreadOut, GetInCover }
    internal struct SquadDecision
    {
        public int Slot, PeerVersion, Episode, CommandVersion, RequestSerial, ThreatVersion, ObservationVersion;
        public SquadAction Action;
        public bool Reply;
        public float SelectedAt;
        public SquadPoint Self, Other, Motion;
        public DialogueTicket Dialogue;
        public bool Exists => Action != SquadAction.None;
    }

    // Only three relationships involving the local human, not a squad-wide AI.
    // Rings have fixed capacity; readers bound evidence age independently of cadence.
    internal sealed partial class SquadMemory
    {
        public const int Capacity = 3, HistoryCapacity = 32;
        public const float SamplePeriod = 0.25f, DecisionPeriod = 0.5f, SightPeriod = 1f / 6f;
        public const float Range = 18f, Freshness = 0.8f, DecisionGap = 25f, ThreatLifetime = 8f, OcclusionGrace = 2f;
        private struct Motion { public float Time; public SquadPoint Self, Other; }
        private sealed class Peer
        {
            public readonly Motion[] History = new Motion[HistoryCapacity];
            public int Version, Count, Head, FollowEpisode, CoverEpisode, CommandVersion, ObservationVersion;
            public bool Present, Available, FollowDecided, CoverDecided, CommandDecided;
            public float SampleAt = -1000f, FollowSince = -1f, LastFollow = -1000f, Distance;
            public float HitAt = -1000f, ShotAt = -1000f, FirstShot = -1000f, CommandUntil, CommandAt;
            public int Shots;
            public string Attacker, CoverTarget, CoverEndReason, DecidedCoverTarget;
            public SquadCommand Command;
            public SquadAction Movement;
            public int MoveEpisode, MoveDecided, RequestSerial;
            public float MoveSince = -1f, LastMove = -1000f, Speed;
            public float ClosingSince = -1f, ArrivedAt = -1000f, StoppedSince = -1f;
            public SquadPoint MoveSelf, MoveOther, CommandSelf, CommandOther, StopPoint;
            public float CommandDistance;
            public bool WasMoving;
            public float HiddenSince = -1f, ResumedAt = -1000f;
            public SquadPoint HiddenSelf, LastMotion;
            public readonly Initiative Lead = new Initiative(), Rally = new Initiative(), Hold = new Initiative();
            public float PairedAt = -1000f, MovedAt = -1000f, FarSince = -1f, FarDistance;
            public SquadPoint FarSelf, StopSelf;
            public bool WasPaired, OtherMoving;
            public float LeadAt, HoldAt;
            public DialogueTicket Dialogue;
        }
        private readonly Peer[] _peers = { new Peer(), new Peer(), new Peer() };
        private float _decisionAt, _replyAt, _retreatAt, _manualUntil, _tacticalAt;
        private int _primary = -1;
        private int _requestSerial, _decisionRequestSerial, _threatVersion;
        private string _threatId;
        private SquadPoint _threat;
        private float _threatAt = -1000f;
        private bool _combat;

        public void Bind(int slot)
        {
            Unbind(slot);
            _peers[slot].Present = true;
        }
        public void Unbind(int slot)
        {
            Peer p = _peers[slot];
            p.Version++; p.Present = false;
            Invalidate(p);
            p.FollowDecided = p.CoverDecided = false;
            p.DecidedCoverTarget = null;
            p.LastFollow = -1000f;
            p.MoveDecided = 0; p.LastMove = -1000f;
            p.Lead.Clear(); p.Rally.Clear(); p.Hold.Clear(); p.LeadAt = p.HoldAt = 0; p.Dialogue = default; p.PairedAt = p.MovedAt = -1000f;
            if (_primary == slot) _primary = -1;
        }
        private static void Invalidate(Peer p)
        {
            p.ObservationVersion++;
            p.Available = false; p.Count = p.Head = 0; p.FollowSince = -1f;
            p.FollowEpisode++; p.CoverEpisode++; p.CommandVersion++;
            p.Command = SquadCommand.None; p.CommandDecided = true;
            p.Attacker = p.CoverTarget = null; p.HitAt = p.ShotAt = -1000f; p.Shots = 0;
            p.CoverEndReason = null;
            ResetMovement(p);
            p.Speed = 0f; p.OtherMoving = false; p.ClosingSince = p.StoppedSince = -1f; p.ArrivedAt = -1000f;
            p.HiddenSince = -1f; p.FarSince = -1f; p.WasPaired = false;
            p.Lead.Interrupt(); p.Rally.Interrupt(); p.Hold.Interrupt();
        }
        public void Suspend(float now, bool manual = false)
        {
            for (int i = 0; i < Capacity; i++) Invalidate(_peers[i]);
            ClearThreat();
            _coverNeed.Interrupt();
            _primary = -1;
            if (manual) _manualUntil = _decisionAt = Math.Max(_decisionAt, now + DecisionGap);
        }

        private static void ResetMovement(Peer p)
        {
            p.Movement = SquadAction.None; p.MoveSince = -1f; p.MoveEpisode++;
        }
        private void ClearThreat()
        {
            _threatId = null; _threatAt = -1000f; _threatVersion++;
            for (int i = 0; i < Capacity; i++) ResetMovement(_peers[i]);
            ClearBattleThreat();
        }
        public void Combat(bool active)
        {
            if (_combat && !active) ClearThreat();
            _combat = active;
        }
        // Called only with a living hostile already confirmed by the local view probe.
        // Both old and new player positions are compared to this SAME anchor.
        public void Threat(string id, SquadPoint position, float now, bool focused = true)
        {
            if (string.IsNullOrEmpty(id) || !position.Finite) return;
            // Round-robin checks can refresh this anchor, but must not keep
            // replacing it with peripheral enemies and erasing movement evidence.
            if (!focused && _threatId != null && _threatId != id && now - _threatAt <= ThreatLifetime) return;
            if (_threatId != id || now - _threatAt > ThreatLifetime || (position - _threat).LengthSquared > 36f)
            {
                ClearThreat();
                for (int i = 0; i < Capacity; i++) _peers[i].MoveDecided = 0;
            }
            _threatId = id; _threat = position; _threatAt = now;
        }
        private bool RecentThreat(float now) => _combat && _threatId != null && now - _threatAt <= ThreatLifetime;

        // Preserve existing evidence briefly, without sampling hidden peer transforms
        // or adding the unseen time to any continuous-action threshold.
        public void Obscured(int slot, float now, SquadPoint self)
        {
            Peer p = _peers[slot];
            if (!p.Present || p.Count == 0) return;
            Motion last = p.History[(p.Head + HistoryCapacity - 1) % HistoryCapacity];
            if (!self.Finite || now - p.SampleAt > OcclusionGrace
                || (self - last.Self).LengthSquared > 144f * (now - last.Time) * (now - last.Time))
            { Invalidate(p); return; }
            if (p.HiddenSince < 0f) p.HiddenSince = now;
            p.Available = false; p.HiddenSelf = self;
        }

        public void Sample(int slot, float now, SquadPoint self, SquadPoint other, bool available)
        {
            Peer p = _peers[slot];
            if (!p.Present) return;
            if (!available || !self.Finite || !other.Finite || (other - self).LengthSquared > Range * Range)
            { if (p.Available || p.Count > 0) Invalidate(p); return; }
            if (p.Count > 0)
            {
                Motion last = p.History[(p.Head + HistoryCapacity - 1) % HistoryCapacity];
                float dt = now - last.Time;
                if (dt <= 0f) return;
                // Discontinuities, network teleports and stale samples are not movement evidence.
                bool resume = p.HiddenSince >= 0f && dt <= OcclusionGrace;
                if ((!resume && dt > Freshness) || (self - last.Self).LengthSquared > 144f * dt * dt
                    || (other - last.Other).LengthSquared > 144f * dt * dt) Invalidate(p);
                else if (resume)
                {
                    if (p.FollowSince >= 0f) p.FollowSince += dt;
                    if (p.MoveSince >= 0f) p.MoveSince += dt;
                    p.Lead.Pause(dt); p.Rally.Pause(dt); p.Hold.Interrupt();
                    p.ClosingSince = p.StoppedSince = -1f; p.ArrivedAt = -1000f;
                    p.ResumedAt = now;
                }
            }
            p.HiddenSince = -1f;
            p.Available = true; p.SampleAt = now;
            p.Distance = (float)Math.Sqrt((other - self).LengthSquared);
            Motion old = default;
            bool haveOld = false;
            for (int n = 1; n <= p.Count; n++)
            {
                Motion candidate = p.History[(p.Head + HistoryCapacity - n) % HistoryCapacity];
                if (now - candidate.Time >= 0.75f)
                { old = candidate; haveOld = now - candidate.Time <= (now - p.ResumedAt < 0.75f ? OcclusionGrace + 0.75f : 1.25f); break; }
            }
            p.History[p.Head] = new Motion { Time = now, Self = self, Other = other };
            p.Head = (p.Head + 1) % HistoryCapacity;
            p.Count = Math.Min(HistoryCapacity, p.Count + 1);
            bool following = false;
            SquadAction movement = SquadAction.None;
            if (haveOld)
            {
                float dt = now - old.Time;
                SquadPoint mine = self - old.Self, theirs = other - old.Other, separation = other - self;
                float myLength = (float)Math.Sqrt(mine.FlatSquared), theirLength = (float)Math.Sqrt(theirs.FlatSquared);
                p.Speed = myLength / dt;
                p.OtherMoving = theirLength >= 0.6f * dt;
                p.LastMotion = mine;
                if (myLength >= 0.6f * dt && theirLength >= 0.6f * dt)
                {
                    float ahead = SquadPoint.Dot(separation, theirs) / theirLength;
                    float lateralSquared = Math.Max(0f, separation.FlatSquared - ahead * ahead);
                    float oldDistance = (float)Math.Sqrt((old.Other - old.Self).LengthSquared);
                    following = p.Distance >= 2f && p.Distance <= 14f && Math.Abs(separation.Y) < 2f
                        && ahead >= 0.75f && lateralSquared <= 25f
                        && SquadPoint.Dot(mine, theirs) >= 0.65f * myLength * theirLength
                        && Math.Abs(p.Distance - oldDistance) <= 3f * dt;
                }
                // A teammate may remain on overwatch while the local player moves.
                // The threat anchor is still locally observed, never a hidden target update.
                if (RecentThreat(now) && Math.Abs(separation.Y) < 2f && myLength >= 0.6f * dt)
                {
                    int direction = Direction(old.Self, self, mine, myLength, dt);
                    bool compatible = theirLength < 0.6f * dt
                        || Direction(old.Other, other, theirs, theirLength, dt) == direction;
                    if (direction != 0 && compatible)
                        movement = direction > 0 ? SquadAction.Advancing : SquadAction.Retreating;
                }
                SampleRequest(p, now, self, other, mine, myLength, dt);
                SampleInitiatives(p, now, self, other, mine, theirs, myLength, theirLength, dt);
                SampleBattleMotion(slot, p, now, old.Other, other, theirs, theirLength, dt);
            }
            else { p.Speed = 0f; p.Lead.Interrupt(); p.Rally.Interrupt(); p.Hold.Interrupt(); }
            if (following)
            {
                if (now - p.LastFollow >= 8f) p.FollowDecided = false;
                if (p.FollowSince < 0f) { p.FollowSince = now; p.FollowEpisode++; }
                p.LastFollow = now;
            }
            else p.FollowSince = -1f;
            if (movement != SquadAction.None)
            {
                if (now - p.LastMove >= 8f) p.MoveDecided = 0;
                if (p.Movement != movement)
                {
                    p.MoveEpisode++; p.MoveSince = now; p.MoveSelf = self; p.MoveOther = other;
                }
                p.Movement = movement; p.LastMove = now;
            }
            else if (p.Movement != SquadAction.None) ResetMovement(p);
        }

        private int Direction(SquadPoint before, SquadPoint current, SquadPoint delta, float length, float dt)
        {
            SquadPoint toward = _threat - before;
            float distance = (float)Math.Sqrt(toward.FlatSquared);
            if (distance < 3f) return 0;
            float progress = distance - (float)Math.Sqrt((_threat - current).FlatSquared);
            float dot = SquadPoint.Dot(delta, toward);
            if (dot >= 0.55f * length * distance && progress >= 0.4f * dt) return 1;
            if (dot <= -0.55f * length * distance && progress <= -0.4f * dt) return -1;
            return 0;
        }
        private static void SampleRequest(Peer p, float now, SquadPoint self, SquadPoint other,
            SquadPoint delta, float length, float dt)
        {
            if (now > p.CommandUntil) return;
            if (p.Command == SquadCommand.Regroup)
            {
                // Movement of the caller alone cannot count as the local player regrouping.
                SquadPoint toward = p.CommandOther - p.CommandSelf;
                bool closing = p.Speed >= 0.6f && Math.Abs(other.Y - self.Y) < 2f
                    && SquadPoint.Dot(delta, toward) >= 0.65f * length * (float)Math.Sqrt(toward.FlatSquared);
                if (closing)
                {
                    if (p.ClosingSince < 0f) p.ClosingSince = now;
                    if (now - p.ClosingSince >= 1f && (self - p.CommandSelf).FlatSquared >= 4f
                        && p.Distance <= 6f && p.CommandDistance - p.Distance >= 2f) p.ArrivedAt = now;
                }
                else
                {
                    p.ClosingSince = -1f;
                    if (p.Speed >= 0.25f) p.ArrivedAt = -1000f;
                }
            }
            else if (p.Command == SquadCommand.Hold)
            {
                bool stopped = p.WasMoving && length <= 0.25f * dt
                    && (self - p.CommandSelf).FlatSquared <= 9f;
                if (!stopped) p.StoppedSince = -1f;
                else if (p.StoppedSince < 0f || (self - p.StopPoint).FlatSquared > 0.25f)
                { p.StoppedSince = now; p.StopPoint = self; }
            }
        }

        public void Hear(int slot, SquadCommand command, float now, float duration, DialogueTicket dialogue = default)
        {
            Peer p = _peers[slot];
            if (!Fresh(p, now) || command == SquadCommand.None || float.IsNaN(duration) || duration < 0f || duration > 20f) return;
            if (p.Command == command && now < p.CommandUntil) return; // repeated packets cannot renew a request
            p.Dialogue = dialogue;
            p.Command = command; p.CommandAt = now + duration + 0.35f; p.CommandUntil = now + duration + 6f;
            p.CommandVersion++; p.CommandDecided = false;
            p.RequestSerial = ++_requestSerial;
            Motion last = p.History[(p.Head + HistoryCapacity - 1) % HistoryCapacity];
            p.CommandSelf = last.Self; p.CommandOther = last.Other; p.CommandDistance = p.Distance;
            p.WasMoving = p.Speed >= 0.6f || now - p.MovedAt <= 3f;
            p.ClosingSince = p.StoppedSince = -1f; p.ArrivedAt = -1000f;
            // Hearing a call never causes an echo of that same call.
            if (command == SquadCommand.Follow) p.Lead.Decided = true;
            if (command == SquadCommand.Regroup) p.Rally.Decided = true;
            if (command == SquadCommand.Hold) p.Hold.Decided = true;
        }
        public void OtherAcknowledged(float now)
        {
            // Only suppress responses to a recently heard request; never turn a
            // received acknowledgement into a new request or infer its target.
            for (int i = 0; i < Capacity; i++)
            {
                Peer p = _peers[i];
                if (p.Command != SquadCommand.None && now <= p.CommandUntil)
                {
                    p.CommandDecided = true; p.CommandVersion++;
                    if (p.Command == SquadCommand.Follow) p.FollowDecided = true;
                    else if (p.Command == SquadCommand.Cover) { p.CoverDecided = true; p.DecidedCoverTarget = p.CoverTarget; }
                    else if (p.Command == SquadCommand.Advance) p.MoveDecided |= 1;
                    else if (p.Command == SquadCommand.Retreat) p.MoveDecided |= 2;
                }
            }
        }
        public bool Hit(int slot, float now, string attacker)
        {
            Peer p = _peers[slot];
            if (!Fresh(p, now) || string.IsNullOrEmpty(attacker)) return false;
            Danger(now, attacker);
            if (p.Attacker != attacker || now - p.HitAt > 8f)
            {
                p.CoverEndReason = "SquadCoverAttackChanged";
                p.CoverEpisode++; p.CoverDecided = false; p.Shots = 0; p.ShotAt = -1000f;
            }
            p.Attacker = attacker; p.HitAt = now;
            return true;
        }
        public void Shot(float now, string visibleTarget)
        {
            for (int i = 0; i < Capacity; i++)
            {
                Peer p = _peers[i];
                if (p.CoverTarget != visibleTarget)
                {
                    p.CoverEndReason = string.IsNullOrEmpty(visibleTarget) ? "SquadCoverTargetUnobserved" : "SquadCoverTargetChanged";
                    p.CoverTarget = visibleTarget; p.CoverEpisode++;
                    p.Shots = 0; p.ShotAt = -1000f;
                }
                if (string.IsNullOrEmpty(visibleTarget)) continue;
                if (!Fresh(p, now)) continue;
                bool request = p.Command == SquadCommand.Cover && now <= p.CommandUntil;
                bool movingTeammate = RecentThreat(now) && visibleTarget == _threatId && p.OtherMoving;
                bool matchingHit = p.Attacker == visibleTarget && now - p.HitAt <= 8f;
                if (!matchingHit && !request && !movingTeammate) continue;
                // A cover request alone supports firing at a visible threat. A
                // witnessed attack additionally associates it with that enemy.
                if (p.Attacker != null && now - p.HitAt <= 8f && !matchingHit) continue;
                if (now - p.ShotAt < 0.15f) continue;
                if (now - p.ShotAt > 2f)
                {
                    p.CoverEndReason = "SquadCoverFireExpired";
                    p.Shots = 0; p.FirstShot = now; p.CoverEpisode++;
                    // A firing pause does not re-arm speech about the same attack.
                }
                p.Shots++; p.ShotAt = now;
            }
        }
        public void TargetDown(string id)
        {
            if (_threatId == id) ClearThreat();
            for (int i = 0; i < Capacity; i++)
            {
                Peer p = _peers[i];
                if (p.CoverTarget != id) continue;
                p.CoverEndReason = "SquadCoverTargetDown";
                p.CoverEpisode++; p.CoverTarget = null; p.Shots = 0; p.ShotAt = -1000f;
            }
        }
        private static bool Fresh(Peer p, float now) => p.Present && p.Available && now - p.SampleAt <= Freshness;
        private static bool Following(Peer p, float now) => Fresh(p, now) && p.FollowSince >= 0f && now - p.FollowSince >= 2.5f;
        private bool Covering(Peer p, float now) => Fresh(p, now) && p.Shots >= 2
            && p.ShotAt - p.FirstShot >= 0.2f && now - p.ShotAt <= 2f
            && (now - p.HitAt <= 8f || p.Command == SquadCommand.Cover && now <= p.CommandUntil
                || RecentThreat(now) && p.CoverTarget == _threatId && p.OtherMoving);

        private static SquadAction RequestedAction(SquadCommand command)
        {
            switch (command)
            {
                case SquadCommand.Follow: return SquadAction.Following;
                case SquadCommand.Cover: return SquadAction.Covering;
                case SquadCommand.Advance: return SquadAction.Advancing;
                case SquadCommand.Retreat: return SquadAction.Retreating;
                case SquadCommand.Regroup: return SquadAction.Regrouping;
                case SquadCommand.Hold: return SquadAction.Holding;
                case SquadCommand.Praise: return SquadAction.Compliment;
                default: return SquadAction.None;
            }
        }
        private bool ActionValid(Peer p, SquadAction action, float now, bool weaponReady)
        {
            if (!Fresh(p, now)) return false;
            if (action == SquadAction.Following) return Following(p, now);
            if (action == SquadAction.Covering) return weaponReady && Covering(p, now);
            Motion last = p.History[(p.Head + HistoryCapacity - 1) % HistoryCapacity];
            if (Math.Abs(last.Other.Y - last.Self.Y) >= 2f) return false;
            if (action == SquadAction.Compliment) return p.Command == SquadCommand.Praise && now <= p.CommandUntil;
            if (action >= SquadAction.Leading) return InitiativeValid(p, action, now);
            if (action == SquadAction.Advancing || action == SquadAction.Retreating)
                return RecentThreat(now) && p.Movement == action && p.MoveSince >= 0f && now - p.MoveSince >= 2f
                    && (last.Self - p.MoveSelf).FlatSquared >= 4f;
            if (now > p.CommandUntil) return false;
            if (action == SquadAction.Regrouping)
                return p.Command == SquadCommand.Regroup && p.Distance <= 6f && now - p.ArrivedAt <= 1.5f;
            if (action == SquadAction.Holding)
                return p.Command == SquadCommand.Hold && p.StoppedSince >= 0f && now - p.StoppedSince >= 1.5f
                    && p.Speed < 0.25f && (last.Self - p.StopPoint).FlatSquared <= 0.25f;
            return false;
        }
        private int Episode(Peer p, SquadAction action)
            => BattleAction(action) ? BattleEpisode(p, action)
                : action == SquadAction.Following ? p.FollowEpisode : action == SquadAction.Covering ? p.CoverEpisode
                : action == SquadAction.Advancing || action == SquadAction.Retreating ? p.MoveEpisode
                : action == SquadAction.RequestCover ? _coverNeed.Version : For(p, action)?.Version ?? p.CommandVersion;
        private static int MoveBit(SquadAction action) => action == SquadAction.Advancing ? 1 : 2;
        private bool Decided(Peer p, SquadAction action)
            => BattleAction(action) ? BattleDecided(p, action)
                : action == SquadAction.Following ? p.FollowDecided : action == SquadAction.Covering ? p.CoverDecided && p.DecidedCoverTarget == p.CoverTarget
                : action == SquadAction.RequestCover ? _coverNeed.Decided : For(p, action)?.Decided
                    ?? (p.MoveDecided & MoveBit(action)) != 0;
        public SquadDecision Decide(float now, bool expressions, bool replies, bool weaponReady, bool survival = false, bool healing = false)
        {
            if (now < _manualUntil) { Silence = SquadSilence.ManualPriority; return default; }
            Silence = SquadSilence.NoPeer;
            SquadDecision best = default;
            float score = float.MinValue;
            for (int i = 0; i < Capacity; i++)
            {
                Peer p = _peers[i];
                if (!p.Present) continue;
                if (!Fresh(p, now)) { if (Silence == SquadSilence.NoPeer) Silence = SquadSilence.NoFreshObservation; continue; }
                Silence = SquadSilence.NoMatchingBehavior;
                SquadAction requestedAction = RequestedAction(p.Command);
                bool requested = replies && !p.CommandDecided && now <= p.CommandUntil
                    && (!survival || requestedAction == SquadAction.Retreating)
                    && ActionValid(p, requestedAction, now, weaponReady);
                SquadAction battle = BattleCandidate(i, p, now, weaponReady);
                SquadAction action = EmergencyAction(battle) ? battle : requested ? requestedAction
                    : ActionValid(p, SquadAction.RequestCover, now, weaponReady) ? SquadAction.RequestCover
                    : ActionValid(p, SquadAction.Retreating, now, weaponReady) ? SquadAction.Retreating
                    : battle != SquadAction.None ? battle
                    : weaponReady && Covering(p, now) ? SquadAction.Covering
                    : ActionValid(p, SquadAction.HoldTeam, now, weaponReady) ? SquadAction.HoldTeam
                    : ActionValid(p, SquadAction.Rallying, now, weaponReady) ? SquadAction.Rallying
                    : ActionValid(p, SquadAction.Advancing, now, weaponReady) ? SquadAction.Advancing
                    : ActionValid(p, SquadAction.Leading, now, weaponReady) ? SquadAction.Leading
                    : Following(p, now) ? SquadAction.Following : SquadAction.None;
                if (action == SquadAction.None) continue;
                if (!Allowed(action, new BarkContext { Injury = survival ? InjuryLevel.BadlyInjured : InjuryLevel.Healthy, Healing = healing }))
                { Silence = SquadSilence.Survival; continue; }
                if (requested && now < p.CommandAt) { Silence = SquadSilence.HeardRequestPlaying; continue; }
                bool reply = requested && !EmergencyAction(action);
                if (!reply && !expressions) continue;
                if (!reply && CommandFor(action) != SquadCommand.None && p.Command == CommandFor(action) && now <= p.CommandUntil)
                { Silence = SquadSilence.HeardRequestPlaying; continue; }
                float weight = (reply ? 300f : 0f) + (action == SquadAction.GetInCover ? 500f : action == SquadAction.SpreadOut ? 450f
                    : action == SquadAction.RequestCover ? 160f : action == SquadAction.Retreating ? 150f
                    : action == SquadAction.PushNow ? 125f : action == SquadAction.Suppressing ? 120f
                    : action == SquadAction.Covering ? 100f : action == SquadAction.HoldTeam ? 70f
                    : action == SquadAction.Rallying ? 60f : action == SquadAction.Advancing ? 50f : action == SquadAction.Leading ? 10f : 0f)
                    - p.Distance + (i == _primary ? 2f : 0f);
                if (weight <= score) continue;
                score = weight;
                Motion observed = p.History[(p.Head + HistoryCapacity - 1) % HistoryCapacity];
                best = new SquadDecision { Slot = i, PeerVersion = p.Version, Action = action, Reply = reply,
                    Episode = Episode(p, action), ThreatVersion = _threatVersion,
                    SelectedAt = now, Self = observed.Self, Other = observed.Other, Motion = p.LastMotion, ObservationVersion = p.ObservationVersion,
                    CommandVersion = p.CommandVersion, RequestSerial = _requestSerial, Dialogue = p.Dialogue };
            }
            if (!best.Exists) return best;
            Peer selected = _peers[best.Slot];
            // Choose the actual strongest relationship first, even if it already
            // spoke (or had no audio). Do not cycle through weaker partners after
            // the shared cooldown while the same formation continues.
            if (!best.Reply && (Decided(selected, best.Action)
                || best.Action == SquadAction.Leading && now < selected.LeadAt
                || best.Action == SquadAction.HoldTeam && now < selected.HoldAt)) { Silence = SquadSilence.AlreadyConsidered; return default; }
            // A new real request may replace an unspoken expression. It cannot
            // retry missing audio, bypass manual silence or start an acknowledgement loop.
            bool newReply = best.Reply && selected.RequestSerial > _decisionRequestSerial && now >= _replyAt;
            bool newRetreat = !best.Reply && best.Action == SquadAction.Retreating && now >= _retreatAt;
            bool newCover = !best.Reply && best.Action == SquadAction.RequestCover && now >= _coverAt;
            bool tactical = SquadDialogue.Tactical(best.Action) || BattleTactical(best.Action);
            if (now < (tactical ? _tacticalAt : _decisionAt) && !newReply && !newRetreat && !newCover)
            { Silence = SquadSilence.DecisionCooldown; return default; }
            if (best.Reply && now < _replyAt) { Silence = SquadSilence.ReplyCooldown; return default; }
            if (BattleAction(best.Action)) MarkBattleDecided(selected, best.Action);
            else if (best.Action == SquadAction.Following) selected.FollowDecided = true;
            else if (best.Action == SquadAction.Covering) { selected.CoverDecided = true; selected.DecidedCoverTarget = selected.CoverTarget; }
            else if (best.Action == SquadAction.Advancing || best.Action == SquadAction.Retreating)
                selected.MoveDecided |= MoveBit(best.Action);
            else if (best.Action == SquadAction.RequestCover) { _coverNeed.Decided = true; _coverAt = now + DecisionGap; }
            else if (For(selected, best.Action) is Initiative initiative) initiative.Decided = true;
            if (best.Reply) { selected.CommandDecided = true; _replyAt = now + 8f; }
            if (best.Action == SquadAction.Retreating) _retreatAt = now + DecisionGap;
            _primary = best.Slot;
            _decisionRequestSerial = _requestSerial;
            if (best.Action == SquadAction.Leading) selected.LeadAt = now + 90f;
            if (best.Action == SquadAction.HoldTeam) selected.HoldAt = now + 60f;
            if (tactical) _tacticalAt = now + 8f;
            else if (!best.Reply) _decisionAt = now + DecisionGap;
            // Missing clips consume this opportunity; never cycle through partners.
            Silence = SquadSilence.None;
            return best;
        }
        public bool Valid(SquadDecision d, float now, bool weaponReady, bool replies = true)
            => RejectionReason(d, now, weaponReady, replies) == null;

        public string RejectionReason(SquadDecision d, float now, bool weaponReady, bool replies = true)
        {
            if (!d.Exists || d.Slot < 0 || d.Slot >= Capacity) return "SquadInvalidDecision";
            Peer p = _peers[d.Slot];
            if (p.Version != d.PeerVersion) return "SquadPeerReplaced";
            if (p.ObservationVersion != d.ObservationVersion) return "SquadObservationReset";
            if (d.Reply && p.CommandVersion != d.CommandVersion) return "SquadRequestChanged";
            if (d.Reply && now > p.CommandUntil) return "SquadRequestExpired";
            if (replies && !d.Reply && p.RequestSerial > d.RequestSerial && now <= p.CommandUntil
                && RequestedAction(p.Command) == d.Action) return "SquadRequestPreferred";
            if ((d.Action == SquadAction.Advancing || d.Action == SquadAction.Retreating) && d.ThreatVersion != _threatVersion)
                return "SquadThreatChanged";
            if (!Fresh(p, now)) return Remembered(d, p, now) ? null : "SquadObservationExpired";
            if (BattleAction(d.Action)) return BattleRejectionReason(d, now, weaponReady);
            if ((ActionValid(p, d.Action, now, weaponReady) && Episode(p, d.Action) == d.Episode)
                || BriefMotionContinuity(d, p, now)) return null;
            if (d.Action == SquadAction.Covering)
            {
                if (!weaponReady) return "SquadWeaponUnavailable";
                if (p.CoverEpisode != d.Episode) return p.CoverEndReason ?? "SquadCoverEvidenceChanged";
                return now - p.ShotAt > 2f ? "SquadCoverFireExpired" : "SquadCoverEvidenceChanged";
            }
            if (d.Action == SquadAction.RequestCover)
                return _ownAction == SquadOwnAction.None ? "SquadActionEnded" : "SquadActionChanged";
            return Episode(p, d.Action) != d.Episode ? "SquadMovementEpisodeChanged" : "SquadMovementChanged";
        }

        private bool BriefMotionContinuity(SquadDecision d, Peer p, float now)
        {
            // A selected expression can survive one or two formation-threshold
            // samples, only while actual fresh movement still agrees. This never
            // creates a candidate, renews its TTL, or extends an acknowledgement.
            float dt = now - d.SelectedAt;
            if (d.Reply || dt <= 0f || dt > 0.65f || p.Distance < 2f || p.Distance > 14f) return false;
            if (d.Action != SquadAction.Following && d.Action != SquadAction.Leading
                && d.Action != SquadAction.Advancing && d.Action != SquadAction.Retreating
                && d.Action != SquadAction.Rallying) return false;
            if ((d.Action == SquadAction.Advancing || d.Action == SquadAction.Retreating) && !RecentThreat(now)) return false;
            Motion last = p.History[(p.Head + HistoryCapacity - 1) % HistoryCapacity];
            SquadPoint mine = last.Self - d.Self, theirs = last.Other - d.Other, separation = last.Other - last.Self;
            if (Math.Abs(separation.Y) >= 2f || !Continues(mine, d.Motion, dt)) return false;
            if (d.Action == SquadAction.Rallying)
                return separation.FlatSquared < (d.Other - d.Self).FlatSquared
                    && SquadPoint.Dot(mine, separation) > 0f;
            if (!Continues(theirs, d.Motion, dt)) return false;
            float ahead = SquadPoint.Dot(separation, d.Motion);
            if (d.Action == SquadAction.Following && ahead <= 0f) return false;
            if (d.Action == SquadAction.Leading && ahead >= 0f) return false;
            return true;
        }
        private static bool Continues(SquadPoint movement, SquadPoint direction, float dt)
            => direction.FlatSquared > 0f && Math.Abs(movement.Y) < 2f && movement.LengthSquared <= 144f * dt * dt
                && movement.FlatSquared >= 0.36f * dt * dt
                && SquadPoint.Dot(movement, direction) >= 0.8f * Math.Sqrt(movement.FlatSquared * direction.FlatSquared);
    }
}
