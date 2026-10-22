using System;
using System.Diagnostics;
using BepInEx.Logging;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace ImmersiveVoice
{
    // Unity/Fika adapter. A local player owns at most three peers and one pending
    // expression. No remote controller is told to speak; no position packets.
    internal sealed class SquadObserver : IDisposable
    {
        private sealed class Peer
        {
            public Player Player;
            public string Id;
            public BaseSpeaker Speaker;
            public float SeenAt = -1000f;
            public float ClearAt = -1000f, SpeakingUntil, SpeechRange;
            public bool InView;
            public string Perception;
            public float HitTraceAt = -1000f;
            public SquadLossEvidence LossEvidence;
            public int HeardPhrase, HeardClip;
            public float HeardAt = -1000f, HeardDuration;
            public bool WaitingMetadata;
            public DialogueTicket Requested, Granted;
            public readonly SquadAwareness Awareness = new SquadAwareness();
        }
        private readonly Player _local;
        private readonly GameWorld _world;
        private readonly FikaBridge _fika;
        private readonly Settings _settings;
        private readonly ManualLogSource _log;
        private readonly VoiceJournal _journal;
        private readonly Peer[] _peers = { new Peer(), new Peer(), new Peer() };
        private readonly SquadMemory _memory = new SquadMemory();
        private readonly SquadLossMemory _loss = new SquadLossMemory();
        private readonly ViewProbe _probe = new ViewProbe();
        private SquadGate _sample, _decision, _sight, _roster;
        private int _scanIndex, _sightIndex;
        private bool _active, _disposed, _faulted;
        private float _othersUntil;
        private long _ticks, _elapsed, _maximum;
        private int _samples, _checks, _decisions, _discoveryEntries, _hits, _hitCallbacks, _shots;
        private string _silence;
        private float _traceAt;

        public SquadObserver(Player local, FikaBridge fika, Settings settings, ManualLogSource log, VoiceJournal journal)
        {
            _local = local; _world = local.GameWorld; _fika = fika; _settings = settings; _log = log; _journal = journal;
            if (_fika.Mode(local) != VoiceMode.Solo) _world.OnPersonAdd += OnPersonAdd;
        }
        public bool Enabled => !_disposed && !_faulted && _settings.Director.Value
            && (_settings.SquadCoordination.Value || _settings.TeamReplies.Value || _settings.FriendlyDown.Value) && _fika.Mode(_local) != VoiceMode.Solo;
        public bool OthersSpeaking(float now) => _active && now < _othersUntil;
        private bool Member(Player p) => p != null && p != _local && !_fika.IsAI(p) && !p.IsYourPlayer
            && !string.IsNullOrEmpty(p.ProfileId) && p.GameWorld == _world && ViewProbe.IsAlly(_local, p);
        private bool Available(Peer p) => Member(p.Player) && p.Player.gameObject.activeInHierarchy
            && p.Player.HealthController?.IsAlive == true && !_fika.Downed(p.Player)
            && _world.GetAlivePlayerByProfileID(p.Id) == p.Player;
        private bool Seen(Peer p, float now) => Available(p) && p.InView && now - p.SeenAt <= SquadMemory.Freshness
            && (p.Player.Transform.position - _local.Transform.position).sqrMagnitude <= SquadMemory.Range * SquadMemory.Range;
        private bool Audible(Peer p, float now) => Available(p) && now - p.ClearAt <= SquadMemory.Freshness && now < p.SpeakingUntil
            && (p.Player.Transform.position - _local.Transform.position).sqrMagnitude <= p.SpeechRange * p.SpeechRange;
        private bool Companion(Peer p, float now)
        {
            if (!Available(p)) return false;
            Vector3 delta = p.Player.Transform.position - _local.Transform.position;
            return p.Awareness.Nearby(now, now - p.ClearAt <= SquadMemory.Freshness, delta.magnitude, delta.y);
        }
        private bool Perceived(Peer p, float now) => Seen(p, now) || Audible(p, now) || Companion(p, now);
        public bool Nearby(float now, float range)
        {
            if (!_active || !Enabled) return false;
            for (int i = 0; i < SquadMemory.Capacity; i++)
                if (Perceived(_peers[i], now))
                {
                    Vector3 delta = _peers[i].Player.Transform.position - _local.Transform.position;
                    if (delta.sqrMagnitude <= range * range && Math.Abs(delta.y) < 2f) return true;
                }
            return false;
        }
        public bool ReportNearby(float now)
        {
            if (!_active || !Enabled) return false;
            SquadPoint self = Point(_local.Transform.position);
            for (int i = 0; i < SquadMemory.Capacity; i++)
                if (Available(_peers[i]) && _peers[i].Awareness.ReportNearby(now, self)) return true;
            return false;
        }
        private bool Tracing => _journal.Enabled || _settings.Debug.Value;
        private void Trace(string fields)
        {
            _journal.Record(Time.time, fields);
            if (_settings.Debug.Value) _log.LogInfo(FormattableString.Invariant($"[AutoVoice] Session={_journal.Session} Time={Time.time:F3} {fields}"));
        }

        private void OnPersonAdd(IPlayer player)
        {
            try { if (Enabled) Register(player as Player); }
            catch (Exception ex) { Fail(ex); }
        }
        private void Register(Player player)
        {
            if (!Member(player)) return;
            int empty = -1;
            for (int i = 0; i < SquadMemory.Capacity; i++)
            {
                if (_peers[i].Player == player) return;
                if (_peers[i].Id == player.ProfileId) Remove(i);
                if (_peers[i].Player == null && empty < 0) empty = i;
            }
            if (empty < 0) return;
            Peer p = _peers[empty];
            p.Player = player; p.Id = player.ProfileId; p.WaitingMetadata = false; p.Requested = p.Granted = default;
            _memory.Bind(empty);
            _loss.Bind(empty);
            BindSpeaker(p);
        }
        private void BindSpeaker(Peer p)
        {
            BaseSpeaker speaker = p.Player?.Speaker;
            if (speaker == p.Speaker) return;
            if (p.Speaker != null) p.Speaker.OnPhraseTold -= OnPhrase;
            p.Speaker = speaker;
            if (speaker != null) speaker.OnPhraseTold += OnPhrase;
        }
        private void Remove(int slot, bool forget = true)
        {
            Peer p = _peers[slot];
            if (p.Speaker != null) p.Speaker.OnPhraseTold -= OnPhrase;
            p.Player = null; p.Speaker = null; p.SeenAt = p.ClearAt = -1000f;
            p.InView = false; p.SpeakingUntil = 0f;
            p.Awareness.Clear();
            // Keep a departed teammate's identity in this same bounded slot so
            // later local corpse observation can identify them. Departure alone says nothing.
            if (forget) { p.Id = null; _loss.Bind(slot); }
            _memory.Unbind(slot);
        }
        private void RefreshRoster()
        {
            bool room = false;
            for (int i = 0; i < SquadMemory.Capacity; i++)
            {
                Peer p = _peers[i];
                if (p.Player != null && !Member(p.Player)) Remove(i);
                else if (p.Player != null && _world.GetAlivePlayerByProfileID(p.Id) != p.Player) Remove(i, forget: false);
                if (p.Player == null) room = true;
                else BindSpeaker(p);
            }
            if (!room) return;
            // Existing players may predate the session or finish group setup
            // after OnPersonAdd. Only eight entries per second, never a full raid scan per tick.
            var players = _world.RegisteredPlayers;
            int count = Math.Min(8, players.Count);
            for (int i = 0; i < count; i++)
            {
                if (_scanIndex >= players.Count) _scanIndex = 0;
                Register(players[_scanIndex++] as Player);
                _discoveryEntries++;
            }
        }
        public SquadDecision Tick(float now, BarkContext context, bool weaponReady, SquadOwnAction ownAction)
        {
            long start = Tracing ? Stopwatch.GetTimestamp() : 0;
            try
            {
                if (!Enabled) { Pause(now); return default; }
                _active = true;
                _memory.Combat(context.Combat != CombatPhase.Calm);
                _memory.OwnAction(now, ownAction, context.Combat == CombatPhase.Engaged);
                if (_roster.Take(now, 1f)) RefreshRoster();
                if (_sight.Take(now, SquadMemory.SightPeriod))
                {
                    // One budgeted peer per call. A blocked/distant peer does not
                    // trigger a search for other targets or more raycasts.
                    Peer p = _peers[_sightIndex];
                    _sightIndex = (_sightIndex + 1) % SquadMemory.Capacity;
                    if (Available(p))
                    {
                        _checks++;
                        // One ray still serves this peer. Clear LOS behind the
                        // camera can corroborate an actual received voice, not sight.
                        bool clear = _probe.TeamVisible(_local, p.Player, SquadMemory.Range, requireFacing: false);
                        Vector3 delta = p.Player.Transform.position + Vector3.up * 1.1f - _local.InteractionRay.origin;
                        p.InView = clear && Vector3.Dot(_local.InteractionRay.direction, delta.normalized) >= 0.25f;
                        p.ClearAt = clear ? now : -1000f;
                        if (p.InView) p.SeenAt = now;
                        Vector3 separation = p.Player.Transform.position - _local.Transform.position;
                        p.Awareness.Sight(now, clear, p.InView, separation.magnitude, separation.y);
                    }
                    else { p.SeenAt = p.ClearAt = -1000f; p.InView = false; }
                }
                if (_sample.Take(now, SquadMemory.SamplePeriod))
                {
                    _samples++;
                    SquadPoint self = Point(_local.Transform.position);
                    for (int i = 0; i < SquadMemory.Capacity; i++)
                    {
                        Peer p = _peers[i];
                        BindHeardDialogue(p, i, now);
                        bool seen = Seen(p, now);
                        bool heard = !seen && Audible(p, now);
                        bool companion = !seen && !heard && Companion(p, now);
                        if (seen || heard || companion)
                        {
                            SquadPoint other = Point(p.Player.Transform.position);
                            _memory.Sample(i, now, self, other, true);
                            p.Awareness.ObserveReport(now, self, other);
                            if (seen) _loss.Alive(i);
                        }
                        else if (Available(p)) _memory.Obscured(i, now, self);
                        else { _memory.Sample(i, now, self, default, false); p.Awareness.Clear(); }
                        if (Tracing)
                        {
                            string perception = seen ? "Visible" : heard ? "Audible" : companion ? "NearbyKnown"
                                : Available(p) ? "Unobserved" : "Unavailable";
                            if (p.Perception != perception)
                            { p.Perception = perception; Trace($"Event=SquadPerception Peer={i} State={perception}"); }
                        }
                    }
                }
                if (!_decision.Take(now, SquadMemory.DecisionPeriod)) return default;
                if (context.RecentlyHit || OthersSpeaking(now))
                { Silence(now, context.RecentlyHit ? "RecentDamage" : "NearbySpeech"); return default; }
                _decisions++;
                SquadDecision decision = _memory.Decide(now, _settings.SquadCoordination.Value, _settings.TeamReplies.Value,
                    weaponReady, context.Survival, context.Healing);
                if (Tracing) Silence(now, decision.Exists ? "Selected" : _memory.Silence.ToString());
                return decision;
            }
            catch (Exception ex) { Fail(ex); return default; }
            finally
            {
                if (start != 0)
                {
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    _ticks++; _elapsed += elapsed; _maximum = Math.Max(_maximum, elapsed);
                }
            }
        }
        private static SquadPoint Point(Vector3 p) => new SquadPoint(p.x, p.y, p.z);
        private void Silence(float now, string reason)
        {
            if (!Tracing || reason == _silence || now < _traceAt) return;
            _silence = reason; _traceAt = now + 1f;
            Trace($"Event=SquadEvaluation Decision={(reason == "Selected" ? "Selected" : "Silent")} Reason={reason} Primary={_memory.PrimarySlot}");
            for (int i = 0; i < SquadMemory.Capacity; i++)
            { string evidence = _memory.Evidence(i, now); if (evidence != null) Trace(evidence); }
        }
        public string Subject(SquadDecision d) => d.Exists ? _peers[d.Slot].Id : null;
        public string RejectionReason(SquadDecision d, float now, bool weaponReady)
        {
            if (!_active || !Enabled) return "SquadInactive";
            if (!d.Exists || d.Slot < 0 || d.Slot >= SquadMemory.Capacity) return "SquadInvalidDecision";
            if (!(d.Reply ? _settings.TeamReplies.Value : _settings.SquadCoordination.Value)) return "SquadFeatureDisabled";
            if (!Available(_peers[d.Slot])) return "SquadPeerUnavailable";
            if (d.Reply && d.Dialogue.Exists)
            {
                int winner = Plugin.Dialogue?.Winner(d.Dialogue, now) ?? -1;
                if (winner < 0 || winner > 0 && winner != _fika.NetworkId(_local)) return "DialogueAssignedElsewhere";
            }
            return _memory.RejectionReason(d, now, weaponReady, _settings.TeamReplies.Value);
        }
        public void Observe(float now, VisibleContact seen)
        {
            if (!_active || !Enabled || seen == null || seen.Distance > SquadMemory.Range) return;
            for (int i = 0; i < SquadMemory.Capacity; i++)
            {
                Peer p = _peers[i];
                if (p.Id != seen.Id) continue;
                if (seen.Alive && seen.Ally) _loss.Alive(i);
                else if (seen.Corpse && !Available(p))
                {
                    SquadLossEvidence evidence = _loss.Observe(i, now, true);
                    if (Tracing && p.LossEvidence != evidence)
                    { p.LossEvidence = evidence; Trace($"Event=SquadLossEvidence Peer={i} Evidence={evidence}"); }
                }
                return;
            }
        }
        public SquadLoss Loss(float now) => _active && Enabled && _settings.FriendlyDown.Value ? _loss.Decide(now) : default;
        public bool Valid(SquadLoss loss, float now) => _active && Enabled && _settings.FriendlyDown.Value && _loss.Valid(loss, now);
        public void Hit(Player victim, DamageInfo info)
        {
            if (!_active || !Enabled || victim == null || info.Damage <= 0f || !VoiceDirector.IsCombatDamage(info.DamageType)) return;
            try
            {
                float now = Time.time;
                for (int i = 0; i < SquadMemory.Capacity; i++)
                    if (_peers[i].Player == victim)
                    {
                        _hitCallbacks++;
                        IPlayer attacker = info.Player?.iPlayer;
                        string rejected = !Perceived(_peers[i], now) ? "PeerNotPerceived"
                            : attacker == null || string.IsNullOrEmpty(attacker.ProfileId) ? "AttackerUnknown"
                            : attacker.ProfileId == _local.ProfileId || ViewProbe.IsAlly(_local, attacker) ? "FriendlyAttacker" : null;
                        if (rejected == null && !_memory.Hit(i, now, attacker.ProfileId)) rejected = "PeerSampleNotFresh";
                        if (rejected != null)
                        {
                            if (Tracing && now - _peers[i].HitTraceAt >= 0.8f)
                            { _peers[i].HitTraceAt = now; Trace($"Event=SquadHitRejected Peer={i} Reason={rejected} TotalCallbacks={_hitCallbacks}"); }
                            return;
                        }
                        _hits++;
                        if (Tracing && now - _peers[i].HitTraceAt >= 0.8f)
                        { _peers[i].HitTraceAt = now; Trace($"Event=SquadHit Peer={i} Evidence={(Seen(_peers[i], now) ? "VisibleCombatImpact" : "NearbyKnownCombatImpact")} TotalCallbacks={_hitCallbacks} AcceptedCallbacks={_hits}"); }
                        return;
                    }
            }
            catch (Exception ex) { Fail(ex); }
        }
        public void Shot(float now, VisibleContact target)
        {
            if (!_active || !Enabled) return;
            _memory.Shot(now, target != null && target.Alive && !target.Ally ? target.Id : null); _shots++;
        }
        public void TargetDown(string id) { if (_active) _memory.TargetDown(id); }
        public void Threat(float now, VisibleContact seen, bool focused)
        {
            if (_active && Enabled && seen?.Player != null && seen.Alive && !seen.Ally)
                _memory.Threat(seen.Id, Point(seen.Player.Transform.position), now, focused);
        }
        private void OnPhrase(EPhraseTrigger phrase, TaggedClip clip, TagBank bank, BaseSpeaker speaker)
        {
            if (!_active || !Enabled || clip?.Clip == null || clip.Length <= 0f) return;
            try
            {
                float now = Time.time;
                for (int i = 0; i < SquadMemory.Capacity; i++)
                {
                    Peer p = _peers[i];
                    if (p.Speaker != speaker || !Available(p) || now - p.ClearAt > SquadMemory.Freshness
                        || (p.Player.Transform.position - _local.Transform.position).sqrMagnitude > clip.Falloff * clip.Falloff) continue;
                    bool physiological = phrase == EPhraseTrigger.OnAgony || phrase == EPhraseTrigger.OnBeingHurt
                        || phrase == EPhraseTrigger.OnBreath || phrase == EPhraseTrigger.OnDeath;
                    if (physiological) return;
                    p.SpeakingUntil = now + clip.Length;
                    p.SpeechRange = Math.Min(clip.Falloff, SquadMemory.Range);
                    if (!Audible(p, now)) return;
                    p.Awareness.Heard(now);
                    _memory.Sample(i, now, Point(_local.Transform.position), Point(p.Player.Transform.position), true);
                    _othersUntil = Math.Max(_othersUntil, now + clip.Length + 0.35f);
                    // Manual Roger retains its conservative suppression. An
                    // unrelated Going/Covering must not cancel another call;
                    // automatic replies are coordinated by their exact ticket.
                    if (phrase == EPhraseTrigger.Roger) _memory.OtherAcknowledged(now);
                    SquadCommand command = SquadDialogue.Command((int)phrase);
                    if (command != SquadCommand.None && _settings.TeamReplies.Value)
                    {
                        p.HeardPhrase = (int)phrase; p.HeardClip = clip.NetId;
                        p.HeardAt = now; p.HeardDuration = clip.Length; p.WaitingMetadata = true;
                        BindHeardDialogue(p, i, now);
                    }
                    if (Tracing) Trace(FormattableString.Invariant($"Event=SquadHeard Peer={i} Phrase={phrase} Command={command} Duration={clip.Length:F2} Evidence={(Seen(p, now) ? "VisibleAndAudible" : "Audible")} ActionRequired=True"));
                    return;
                }
            }
            catch (Exception ex) { Fail(ex); }
        }
        private void BindHeardDialogue(Peer p, int slot, float now)
        {
            if (!p.WaitingMetadata) return;
            if (now - p.HeardAt > 2f || !Available(p))
            {
                p.WaitingMetadata = false;
                if (Tracing) Trace("Event=SquadDialogue Decision=Suppressed Reason=MetadataUnavailable");
                return;
            }
            DialogueTicket ticket = Plugin.Dialogue?.Match(_fika.NetworkId(p.Player), p.HeardPhrase, p.HeardClip, p.HeardAt, now) ?? default;
            if (!ticket.Exists) return;
            _memory.Hear(slot, SquadDialogue.Command(p.HeardPhrase), p.HeardAt, p.HeardDuration, ticket);
            p.WaitingMetadata = false;
            if (Tracing) Trace($"Event=SquadDialogue Decision=Observed Reason=HeardCall Peer={slot} Sequence={ticket.Sequence}");
        }
        public void LocalSpoken(EPhraseTrigger phrase, TaggedClip clip)
        {
            if (!Enabled || clip?.Clip == null) return;
            Plugin.Dialogue?.Publish(_fika.NetworkId(_local), (int)phrase, clip.NetId, clip.Length);
        }
        public string DialogueGate(SquadDecision decision, float now)
        {
            if (!decision.Reply) return null;
            FikaDialogueNetwork net = Plugin.Dialogue;
            if (net == null || !net.Ready || !decision.Dialogue.Exists) return "DialogueUnavailable";
            int actor = _fika.NetworkId(_local), winner = net.Winner(decision.Dialogue, now);
            Peer p = _peers[decision.Slot];
            if (winner == actor)
            {
                if (!p.Granted.Same(decision.Dialogue))
                {
                    p.Granted = decision.Dialogue;
                    if (Tracing) Trace($"Event=SquadDialogue Decision=Granted Reason=HostSelected Peer={decision.Slot} Sequence={decision.Dialogue.Sequence}");
                }
                return null;
            }
            if (winner != 0) return "DialogueAssignedElsewhere";
            if (!p.Requested.Same(decision.Dialogue))
            {
                p.Requested = decision.Dialogue;
                net.Request(decision.Dialogue, actor);
                if (Tracing) Trace($"Event=SquadDialogue Decision=Waiting Reason=HostGrant Peer={decision.Slot} Sequence={decision.Dialogue.Sequence}");
            }
            return "DialogueAwaitingGrant";
        }
        public void Pause(float now, bool manual = false)
        {
            if (_active || manual)
            {
                _memory.Suspend(now, manual);
                _loss.Pause();
                for (int i = 0; i < SquadMemory.Capacity; i++)
                {
                    _peers[i].SeenAt = _peers[i].ClearAt = -1000f; _peers[i].InView = false;
                    _peers[i].SpeakingUntil = 0f; _peers[i].Awareness.Clear(); _peers[i].WaitingMetadata = false;
                }
            }
            _active = false; _othersUntil = 0f;
        }
        private void Fail(Exception ex)
        {
            _faulted = true;
            if (Tracing) Trace("Event=Fault Component=Squad Reason=" + ex.GetType().Name);
            Pause(Time.time);
            _log.LogError("Squad voice disabled for this session: " + ex.Message);
        }
        public void Dispose()
        {
            if (_disposed) return;
            Pause(Time.time); _disposed = true;
            _world.OnPersonAdd -= OnPersonAdd;
            for (int i = 0; i < SquadMemory.Capacity; i++) Remove(i);
            if (Tracing)
            {
                double unit = 1000d / Stopwatch.Frequency;
                Trace(FormattableString.Invariant($"Event=SquadSummary Samples={_samples} SightChecks={_checks} Decisions={_decisions} DiscoveryEntries={_discoveryEntries} Hits={_hits} HitCallbacks={_hitCallbacks} Shots={_shots} MeasuredTicks={_ticks} AverageMs={(_ticks == 0 ? 0 : _elapsed * unit / _ticks):F4} MaxMs={_maximum * unit:F4}"));
                FikaDialogueNetwork net = Plugin.Dialogue;
                if (net != null) Trace($"Event=SquadDialogueSummary Ready={net.Ready} Sent={net.Sent} Received={net.Received} Grants={net.Grants}");
            }
        }
    }
}
