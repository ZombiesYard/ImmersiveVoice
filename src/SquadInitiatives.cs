using System;

namespace ImmersiveVoice
{
    internal enum SquadOwnAction { None, Reloading, WeaponFault, Healing }
    internal enum SquadSilence { None, NoPeer, NoFreshObservation, NoMatchingBehavior, AlreadyConsidered,
        DecisionCooldown, ReplyCooldown, ManualPriority, Survival, HeardRequestPlaying }

    internal sealed partial class SquadMemory
    {
        private sealed class Initiative
        {
            public float Since = -1f, Last = -1000f;
            public int Version;
            public bool Decided;
            public void Clear() { Interrupt(); Last = -1000f; Decided = false; }
            public void Interrupt() { if (Since >= 0f) Version++; Since = -1f; }
            public void Pause(float duration) { if (Since >= 0f) Since += duration; }
            public void Sample(float now, bool active)
            {
                if (!active) { Interrupt(); return; }
                if (now - Last >= 8f) Decided = false;
                if (Since < 0f) { Since = now; Version++; }
                Last = now;
            }
            public bool Ready(float now, float duration) => Since >= 0f && now - Since >= duration && now - Last <= Freshness;
        }
        private readonly Initiative _coverNeed = new Initiative();
        private SquadOwnAction _ownAction;
        private float _coverAt;
        public SquadSilence Silence { get; private set; }
        public int PrimarySlot => _primary;
        public string Evidence(int slot, float now)
        {
            Peer p = _peers[slot];
            if (!p.Present) return null;
            float observed = p.Available ? now : p.SampleAt;
            return FormattableString.Invariant($"Event=SquadEvidence Peer={slot} PeerVersion={p.Version} Fresh={Fresh(p, now)} ObservationAge={now - p.SampleAt:F2} Distance={p.Distance:F2} LocalSpeed={p.Speed:F2} FollowSeconds={(p.FollowSince < 0f ? -1f : observed - p.FollowSince):F2} LeadSeconds={(p.Lead.Since < 0f ? -1f : observed - p.Lead.Since):F2} Movement={p.Movement} MoveSeconds={(p.MoveSince < 0f ? -1f : observed - p.MoveSince):F2} Shots={p.Shots} HeardCommand={p.Command} CommandRemaining={p.CommandUntil - now:F2} OwnAction={_ownAction} CoverReason={CoverEvidence(p, now)} MoveReason={MoveEvidence(p, now)} LeadRepeatRemaining={Math.Max(0f, p.LeadAt - now):F1}");
        }

        private string CoverEvidence(Peer p, float now)
            => !Fresh(p, now) ? "NoFreshPeer" : p.Shots < 2 ? "InsufficientRelatedFire"
                : now - p.ShotAt > 2f ? "FireExpired" : Covering(p, now) ? "Ready" : "NoSupportContext";
        private string MoveEvidence(Peer p, float now)
            => !RecentThreat(now) ? "NoRecentThreat" : !Fresh(p, now) ? "NoFreshPeer"
                : p.Movement == SquadAction.None ? "DirectionOrSpeed" : p.MoveSince < 0 || now - p.MoveSince < 2f ? "MovementTooBrief" : "Ready";

        public void OwnAction(float now, SquadOwnAction action, bool engaged)
        {
            if (_ownAction != action) _coverNeed.Interrupt();
            _ownAction = action;
            _coverNeed.Sample(now, engaged && action != SquadOwnAction.None);
        }
        private static void SampleInitiatives(Peer p, float now, SquadPoint self, SquadPoint other,
            SquadPoint mine, SquadPoint theirs, float myLength, float theirLength, float dt)
        {
            SquadPoint separation = other - self;
            bool sameFloor = Math.Abs(separation.Y) < 2f;
            bool paired = sameFloor && p.Distance >= 2f && p.Distance <= 14f
                && myLength >= 0.6f * dt && theirLength >= 0.6f * dt
                && SquadPoint.Dot(mine, theirs) >= 0.65f * myLength * theirLength;
            bool leading = false;
            if (paired)
            {
                float behind = -SquadPoint.Dot(separation, mine) / myLength;
                float lateral = Math.Max(0f, separation.FlatSquared - behind * behind);
                leading = behind >= 0.75f && lateral <= 25f;
                p.PairedAt = now; p.WasPaired = true;
            }
            p.Lead.Sample(now, leading);
            if (p.Speed >= 0.6f) { p.MovedAt = now; p.StopSelf = self; }
            bool holding = sameFloor && p.WasPaired && now - p.PairedAt <= 8f && now - p.MovedAt <= 5f
                && p.Speed < 0.25f && p.Distance <= 10f && (self - p.StopSelf).FlatSquared <= 1f;
            p.Hold.Sample(now, holding);

            if (sameFloor && p.WasPaired && now - p.PairedAt <= 20f && p.Distance >= 8f)
            {
                if (p.FarSince < 0f || p.Distance > p.FarDistance)
                { p.FarSince = now; p.FarDistance = p.Distance; p.FarSelf = self; }
            }
            else if (p.Distance < 6f || now - p.PairedAt > 20f) p.FarSince = -1f;
            bool rally = sameFloor && p.FarSince >= 0f && p.Distance > 6f && p.Distance <= 14f
                && p.FarDistance - p.Distance >= 2f && (self - p.FarSelf).FlatSquared >= 4f
                && p.Speed >= 0.6f && SquadPoint.Dot(mine, separation) >= 0.65f * myLength * (float)Math.Sqrt(separation.FlatSquared);
            p.Rally.Sample(now, rally);
        }
        private bool InitiativeValid(Peer p, SquadAction action, float now)
        {
            if (action == SquadAction.Leading) return p.Lead.Ready(now, 2.5f);
            if (action == SquadAction.Rallying) return p.Rally.Ready(now, 1f);
            if (action == SquadAction.HoldTeam) return p.Hold.Ready(now, 1.5f);
            return action == SquadAction.RequestCover && p.Distance <= 12f && _coverNeed.Ready(now, 0.75f);
        }
        private static SquadCommand CommandFor(SquadAction action)
            => action == SquadAction.Leading ? SquadCommand.Follow : action == SquadAction.RequestCover ? SquadCommand.Cover
                : action == SquadAction.Rallying ? SquadCommand.Regroup : action == SquadAction.HoldTeam ? SquadCommand.Hold : SquadCommand.None;
        private static Initiative For(Peer p, SquadAction action)
            => action == SquadAction.Leading ? p.Lead : action == SquadAction.Rallying ? p.Rally
                : action == SquadAction.HoldTeam ? p.Hold : null;
        private bool Remembered(SquadDecision d, Peer p, float now)
        {
            if (p.HiddenSince < 0f || now - p.SampleAt > OcclusionGrace || p.Count == 0) return false;
            if (d.Action != SquadAction.Following && d.Action != SquadAction.Leading
                && d.Action != SquadAction.Advancing && d.Action != SquadAction.Retreating) return false;
            Motion last = p.History[(p.Head + HistoryCapacity - 1) % HistoryCapacity];
            SquadPoint moved = p.HiddenSelf - last.Self;
            float elapsed = now - last.Time;
            // Continue only an already-selected line while the local player is
            // still moving in the observed direction. No new candidates in fog.
            if (moved.FlatSquared < 0.36f * elapsed * elapsed || Math.Abs(moved.Y) > 2f
                || (p.HiddenSelf - last.Other).LengthSquared > Range * Range
                || SquadPoint.Dot(moved, p.LastMotion) < 0.8f * Math.Sqrt(moved.FlatSquared * p.LastMotion.FlatSquared)) return false;
            if ((d.Action == SquadAction.Advancing || d.Action == SquadAction.Retreating)
                && (!RecentThreat(now) || _threatVersion != d.ThreatVersion)) return false;
            return Episode(p, d.Action) == d.Episode;
        }
        public static bool Allowed(SquadAction action, BarkContext context)
            => (EmergencyAction(action) || !context.RecentlyHit)
                && (!context.Healing || action == SquadAction.RequestCover)
                && (!context.Survival || action == SquadAction.Retreating || action == SquadAction.RequestCover
                    || EmergencyAction(action));
    }
}
