using System;

namespace ImmersiveVoice
{
    // Evidence for short fire-and-movement and emergency formation calls. All
    // storage is bounded by the three relationships a local player can have in
    // a four-human squad. Callers must only report observed, real events.
    internal sealed partial class SquadMemory
    {
        private const float EmergencyWindow = 3.25f;
        private struct BattlePeer
        {
            public int PeerVersion, ObservationVersion, FireEpisode, AdvanceEpisode;
            public string FireTarget;
            public float FirstShotAt, ShotAt, AdvanceSince, AdvanceAt;
            public int Shots;
            public bool FireDecided;
        }

        private readonly BattlePeer[] _battlePeers = new BattlePeer[Capacity];
        private string _dangerSource;
        private float _dangerAt = -1000f;
        private int _dangerEpisode;
        private bool _dangerDecided;

        private static bool BattleAction(SquadAction action)
            => action == SquadAction.PushNow || action == SquadAction.Suppressing
                || action == SquadAction.SpreadOut || action == SquadAction.GetInCover;

        private static bool EmergencyAction(SquadAction action)
            => action == SquadAction.SpreadOut || action == SquadAction.GetInCover;

        private static bool BattleTactical(SquadAction action) => BattleAction(action);

        private void ClearBattleThreat()
        {
            _dangerSource = null;
            _dangerAt = -1000f;
            _dangerEpisode++;
            _dangerDecided = false;
            for (int i = 0; i < Capacity; i++)
            {
                ref BattlePeer b = ref _battlePeers[i];
                b.FireTarget = null; b.Shots = 0; b.FirstShotAt = b.ShotAt = -1000f;
                b.AdvanceSince = -1f; b.AdvanceAt = -1000f;
                b.FireEpisode++; b.AdvanceEpisode++; b.FireDecided = false;
            }
        }

        private void SampleBattleMotion(int slot, Peer p, float now, SquadPoint before,
            SquadPoint current, SquadPoint delta, float length, float dt)
        {
            ref BattlePeer b = ref _battlePeers[slot];
            if (b.PeerVersion != p.Version || b.ObservationVersion != p.ObservationVersion)
            {
                b = new BattlePeer
                {
                    PeerVersion = p.Version,
                    ObservationVersion = p.ObservationVersion,
                    FirstShotAt = -1000f,
                    ShotAt = -1000f,
                    AdvanceSince = -1f,
                    AdvanceAt = -1000f
                };
            }
            bool advancing = RecentThreat(now) && length >= 0.6f * dt
                && Direction(before, current, delta, length, dt) > 0;
            if (advancing)
            {
                if (b.AdvanceSince < 0f) { b.AdvanceSince = now; b.AdvanceEpisode++; }
                b.AdvanceAt = now;
            }
            else if (b.AdvanceSince >= 0f)
            {
                b.AdvanceSince = -1f; b.AdvanceAt = -1000f; b.AdvanceEpisode++;
            }
        }

        // A peer shot is accepted only after the integration layer observed
        // positive firearm damage from this human peer to this visible target.
        public bool PeerShot(int slot, float now, string visibleTarget)
        {
            if (slot < 0 || slot >= Capacity) return false;
            Peer p = _peers[slot];
            if (!Fresh(p, now)) return false;
            ref BattlePeer b = ref _battlePeers[slot];
            bool sameObservation = b.PeerVersion == p.Version && b.ObservationVersion == p.ObservationVersion;
            if (!sameObservation)
            {
                b = new BattlePeer
                {
                    PeerVersion = p.Version,
                    ObservationVersion = p.ObservationVersion,
                    FirstShotAt = -1000f,
                    ShotAt = -1000f,
                    AdvanceSince = -1f,
                    AdvanceAt = -1000f
                };
            }
            if (string.IsNullOrEmpty(visibleTarget) || !RecentThreat(now) || visibleTarget != _threatId)
            {
                if (b.Shots > 0 || b.FireTarget != null)
                {
                    b.FireTarget = null; b.Shots = 0; b.FirstShotAt = b.ShotAt = -1000f;
                    b.FireEpisode++; b.FireDecided = false;
                }
                return false;
            }
            if (b.FireTarget != visibleTarget || now - b.ShotAt > 2f)
            {
                int episode = b.FireEpisode + 1;
                float advanceSince = b.AdvanceSince, advanceAt = b.AdvanceAt;
                int advanceEpisode = b.AdvanceEpisode;
                b = new BattlePeer
                {
                    PeerVersion = p.Version,
                    ObservationVersion = p.ObservationVersion,
                    FireTarget = visibleTarget,
                    FirstShotAt = now,
                    ShotAt = -1000f,
                    AdvanceSince = advanceSince,
                    AdvanceAt = advanceAt,
                    AdvanceEpisode = advanceEpisode,
                    FireEpisode = episode
                };
            }
            if (now - b.ShotAt < 0.15f) return false;
            b.Shots++; b.ShotAt = now;
            return true;
        }

        // Records an actual hostile impact against the local player or a
        // perceived squadmate. A combat flag or nearby enemy alone is not danger.
        public bool Danger(float now, string knownThreat)
        {
            if (string.IsNullOrEmpty(knownThreat) || !RecentThreat(now) || knownThreat != _threatId) return false;
            if (_dangerSource != knownThreat || now - _dangerAt > 8f)
            {
                _dangerSource = knownThreat;
                _dangerEpisode++;
                _dangerDecided = false;
            }
            _dangerAt = now;
            return true;
        }

        private bool PeerAdvanceReady(int slot, float now)
        {
            BattlePeer b = _battlePeers[slot];
            return b.PeerVersion == _peers[slot].Version && b.ObservationVersion == _peers[slot].ObservationVersion
                && b.AdvanceSince >= 0f && now - b.AdvanceSince >= 1.25f && now - b.AdvanceAt <= Freshness;
        }

        private bool PeerFireReady(int slot, float now)
        {
            BattlePeer b = _battlePeers[slot];
            return b.PeerVersion == _peers[slot].Version && b.ObservationVersion == _peers[slot].ObservationVersion
                && b.FireTarget == _threatId && b.Shots >= 2 && b.ShotAt - b.FirstShotAt >= 0.2f
                && now - b.ShotAt <= 2f;
        }

        private int CrowdedPeers(float now)
        {
            int count = 0;
            for (int i = 0; i < Capacity; i++)
            {
                Peer p = _peers[i];
                if (!Fresh(p, now) || p.Distance > 4f || p.Count == 0) continue;
                Motion last = p.History[(p.Head + HistoryCapacity - 1) % HistoryCapacity];
                if (Math.Abs(last.Other.Y - last.Self.Y) < 2f) count++;
            }
            return count;
        }

        private SquadAction BattleCandidate(int slot, Peer p, float now, bool weaponReady)
        {
            if (RecentThreat(now) && _dangerSource == _threatId && now - _dangerAt <= EmergencyWindow)
            {
                if (p.Attacker == _dangerSource && now - p.HitAt <= 1.5f) return SquadAction.GetInCover;
                if (p.Distance <= 4f && CrowdedPeers(now) >= 2) return SquadAction.SpreadOut;
            }
            if (weaponReady && Covering(p, now) && p.CoverTarget == _threatId && PeerAdvanceReady(slot, now))
                return SquadAction.PushNow;
            if (weaponReady && PeerFireReady(slot, now) && ActionValid(p, SquadAction.Advancing, now, true))
                return SquadAction.Suppressing;
            return SquadAction.None;
        }

        private bool BattleActionValid(int slot, SquadAction action, float now, bool weaponReady)
        {
            if (slot < 0 || slot >= Capacity || !Fresh(_peers[slot], now)) return false;
            Peer p = _peers[slot];
            if (action == SquadAction.PushNow)
                return weaponReady && RecentThreat(now) && Covering(p, now) && p.CoverTarget == _threatId
                    && PeerAdvanceReady(slot, now);
            if (action == SquadAction.Suppressing)
                return weaponReady && RecentThreat(now) && PeerFireReady(slot, now)
                    && ActionValid(p, SquadAction.Advancing, now, true);
            if (!RecentThreat(now) || _dangerSource != _threatId || now - _dangerAt > EmergencyWindow) return false;
            if (action == SquadAction.GetInCover)
                return p.Attacker == _dangerSource && now - p.HitAt <= 1.5f;
            return action == SquadAction.SpreadOut && p.Distance <= 4f && CrowdedPeers(now) >= 2;
        }

        private int BattleEpisode(Peer p, SquadAction action)
        {
            int slot = BattleSlot(p);
            if (slot < 0) return -1;
            if (action == SquadAction.PushNow) return p.CoverEpisode;
            if (action == SquadAction.Suppressing) return _battlePeers[slot].FireEpisode;
            return _dangerEpisode;
        }

        private bool BattleDecided(Peer p, SquadAction action)
        {
            int slot = BattleSlot(p);
            if (slot < 0) return true;
            if (action == SquadAction.PushNow) return p.CoverDecided && p.DecidedCoverTarget == p.CoverTarget;
            if (action == SquadAction.Suppressing) return _battlePeers[slot].FireDecided;
            return _dangerDecided;
        }

        private void MarkBattleDecided(Peer p, SquadAction action)
        {
            int slot = BattleSlot(p);
            if (slot < 0) return;
            if (action == SquadAction.PushNow)
            {
                p.CoverDecided = true; p.DecidedCoverTarget = p.CoverTarget;
            }
            else if (action == SquadAction.Suppressing) _battlePeers[slot].FireDecided = true;
            else _dangerDecided = true;
        }

        private int BattleSlot(Peer peer)
        {
            for (int i = 0; i < Capacity; i++) if (ReferenceEquals(_peers[i], peer)) return i;
            return -1;
        }

        private string BattleRejectionReason(SquadDecision decision, float now, bool weaponReady)
        {
            if (decision.ThreatVersion != _threatVersion) return "SquadThreatChanged";
            if (BattleActionValid(decision.Slot, decision.Action, now, weaponReady)
                && BattleEpisode(_peers[decision.Slot], decision.Action) == decision.Episode) return null;
            if ((decision.Action == SquadAction.PushNow || decision.Action == SquadAction.Suppressing) && !weaponReady)
                return "SquadWeaponUnavailable";
            if (decision.Action == SquadAction.PushNow || decision.Action == SquadAction.Suppressing)
                return "SquadFireMovementEnded";
            return now - _dangerAt > EmergencyWindow ? "SquadDangerExpired" : "SquadEmergencyChanged";
        }
    }
}
