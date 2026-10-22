using System;
using System.Diagnostics;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace ImmersiveVoice
{
    // Bounded records contain nearby heard NPCs or witnessed NPC damage.
    // No raid roster, AI brain, new raycast or audio decoding is used.
    internal sealed class NpcReplyObserver
    {
        private sealed class Probe
        {
            public Player Player;
            public TaggedClip Clip;
            public BetterSource Source;
            public EPhraseTrigger Phrase;
            public float Started;
            public void Clear() { Player = null; Clip = null; Source = null; }
        }
        private sealed class Peer
        {
            public Player Player;
            public NpcLine Line;
            public EPhraseTrigger Phrase;
            public float HeardAt, End, Expires, SampleAt, CoverUntil, HitAt;
            public string Attacker;
            public bool Open, Directed;
            public bool ConversationChecked;
            public EnemyConversationDecision Conversation;
            public string LastReason;
            public Vector3 Position;
            public float FollowSince = -1, ApproachSince = -1, AdvanceSince = -1, RetreatSince = -1, HoldSince = -1;
            public NpcActions Actions;
            public readonly NpcTurnMemory Turns = new NpcTurnMemory();
            public void Bind(Player player, float now)
            {
                Player = player; Open = false; Directed = false; ConversationChecked = false; Conversation = default;
                LastReason = null; Attacker = null; CoverUntil = HitAt = -1000;
                HeardAt = SampleAt = now; Position = player.Transform.position; Actions = default;
                FollowSince = ApproachSince = AdvanceSince = RetreatSince = HoldSince = -1;
                Turns.Clear();
            }
        }
        private static readonly EPhraseTrigger[] Phrases = (EPhraseTrigger[])Enum.GetValues(typeof(EPhraseTrigger));
        private readonly Probe[] _probes = new Probe[NpcDialogueRules.Capacity];
        private readonly Peer[] _peers = new Peer[NpcDialogueRules.Capacity];
        private readonly Player _local;
        private readonly FikaBridge _fika;
        private readonly Func<string, float, bool> _knownHostile;
        private readonly Action<string> _trace;
        private readonly NpcReplyMemory _memory = new NpcReplyMemory();
        private readonly EnemyConversationMemory _enemyConversation = new EnemyConversationMemory();
        private readonly System.Random _random = new System.Random();
        private Peer _target;
        private NpcResponse _response;
        private bool _active, _sampled;
        private float _sampleAt, _capacityTraceAt;
        private Vector3 _localPosition;
        private int _capacityDrops;
        private bool _faulted;
        private long _ticks, _elapsed, _maximum;
        private int _captures, _samples;
        private int _callbacks, _rejectedSpeaker, _rejectedAudio, _outOfRange, _humanIgnored, _breathIgnored, _npcCaptures;
        private float _summaryAt;
        public int Version => _memory.Version;
        public float ReadyAt => _memory.ReadyAt;
        public float ExpiresAt => _memory.ExpiresAt;
        public bool Pending => _memory.Pending;
        public VoiceIntent Intent => NpcDialogueRules.Intent(_response);
        public int Priority => NpcDialogueRules.Priority(_response);
        public EPhraseTrigger ReplyPhrase => Output(_response);

        public NpcReplyObserver(Player local, FikaBridge fika, Func<string, float, bool> knownHostile, Action<string> trace)
        {
            _local = local; _fika = fika; _knownHostile = knownHostile; _trace = trace;
            for (int i = 0; i < _peers.Length; i++) { _peers[i] = new Peer(); _probes[i] = new Probe(); }
        }
        public void Active(bool active)
        {
            if (!active)
            {
                if (_active || Pending) Pause();
            }
            else _active = !_faulted;
        }
        private bool Present(Player player) => player != null && player != _local && !player.IsYourPlayer
            && player.GameWorld == _local.GameWorld && player.gameObject.activeInHierarchy && player.HealthController != null;
        private bool Available(Player player) => Present(player) && player.HealthController.IsAlive && !_fika.Downed(player);
        private float Distance(Player player) => Vector3.Distance(_local.Transform.position, player.Transform.position);
        private NpcRelation Relation(Player player, float now)
        {
            WildSpawnType? role = player.Profile?.Info?.Settings?.Role;
            bool scav = role == WildSpawnType.assault || role == WildSpawnType.cursedAssault || role == WildSpawnType.marksman;
            return NpcDialogueRules.Relationship(ViewProbe.IsAlly(_local, player), _knownHostile(player.ProfileId, now),
                _local.Side == EPlayerSide.Usec || _local.Side == EPlayerSide.Bear,
                player.Side == EPlayerSide.Usec || player.Side == EPlayerSide.Bear, scav);
        }
        private static EPhraseTrigger Identify(Player player, TaggedClip clip)
        {
            EPhraseTrigger actual = SpeechObservation.Phrase(player, clip);
            if (actual != EPhraseTrigger.None) return actual;
            // Compatibility for third-party playback outside OnPhraseTold. At most
            // one indexed lookup per enum, never a scan of the banks' clip arrays.
            if (player.Speaker == null || clip.NetId < 0) return EPhraseTrigger.None;
            EPhraseTrigger found = EPhraseTrigger.None;
            foreach (EPhraseTrigger phrase in Phrases)
                if (player.Speaker.PhrasesBanks.TryGetValue(phrase, out var bank) && bank?.Clips != null
                    && clip.NetId < bank.Clips.Length && ReferenceEquals(bank.Clips[clip.NetId], clip))
                {
                    if (found != EPhraseTrigger.None && Meaning(found) != Meaning(phrase)) return EPhraseTrigger.None;
                    found = phrase;
                }
            return found;
        }
        public void Capture(Player player, TaggedClip clip, BetterSource source)
        {
            if (!_active) return;
            try { CaptureCore(player, clip, source); }
            catch (Exception ex) { Fail(ex, "Capture"); }
        }
        private void CaptureCore(Player player, TaggedClip clip, BetterSource source)
        {
            _callbacks++;
            if (!Present(player)) { _rejectedSpeaker++; return; }
            if (clip?.Clip == null || source == null
                || !(clip.Length > NpcReplyRules.ProbeDelay) || !(clip.Length <= 30f)) { _rejectedAudio++; return; }
            if (Distance(player) > NpcReplyRules.Range) { _outOfRange++; return; }
            bool ai = _fika.IsAI(player);
            if (!ai && (!Available(player) || !ViewProbe.IsAlly(_local, player))) { _humanIgnored++; return; }
            EPhraseTrigger phrase = Identify(player, clip);
            // Breathing is not dialogue and must not flood diagnostics or memory.
            if (phrase == EPhraseTrigger.OnBreath) { _breathIgnored++; return; }
            if (!ai && (!Pending || PeerResponse(phrase) == NpcResponse.None)) { _humanIgnored++; return; }
            int free = -1;
            for (int i = 0; i < _probes.Length; i++)
            {
                if (_probes[i].Player == player) { free = i; break; }
                if (_probes[i].Player == null && free < 0) free = i;
            }
            if (free < 0) { _capacityDrops++; return; }
            Probe p = _probes[free];
            p.Player = player; p.Clip = clip; p.Source = source; p.Phrase = phrase; p.Started = Time.time; _captures++;
            if (ai) _npcCaptures++;
        }
        private Peer Remember(Player player, float now)
        {
            Peer free = null;
            foreach (Peer peer in _peers)
            {
                if (peer.Player == player) return peer;
                if (peer != _target && (peer.Player == null || now - peer.HeardAt > 95f)
                    && (free == null || peer.HeardAt < free.HeardAt)) free = peer;
            }
            if (free == null) return null;
            free.Bind(player, now); return free;
        }
        public bool Tick(float now, BarkContext context, bool tracing)
        {
            if (!_active || _faulted) return false;
            long start = tracing ? Stopwatch.GetTimestamp() : 0;
            try { return TickCore(now, context, tracing); }
            catch (Exception ex)
            {
                Fail(ex, "Tick");
                return false;
            }
            finally
            {
                if (tracing)
                {
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    _ticks++; _elapsed += elapsed; _maximum = Math.Max(_maximum, elapsed);
                    if (_ticks >= 100 && now >= _summaryAt)
                    {
                        _summaryAt = now + 60f;
                        _trace(FormattableString.Invariant($"Event=NpcSummary Ticks={_ticks} Captures={_captures} NpcCaptures={_npcCaptures} Callbacks={_callbacks} RejectedSpeaker={_rejectedSpeaker} RejectedAudio={_rejectedAudio} OutOfRange={_outOfRange} HumanIgnored={_humanIgnored} BreathIgnored={_breathIgnored} Samples={_samples} MeanMs={_elapsed * 1000d / Stopwatch.Frequency / _ticks:F4} MaxMs={_maximum * 1000d / Stopwatch.Frequency:F4} Capacity={NpcDialogueRules.Capacity}"));
                    }
                }
            }
        }
        private void Fail(Exception error, string stage)
        {
            Pause(); _faulted = true;
            _trace("Event=NpcFault Stage=" + stage + " " + FaultDetails.Format(error));
        }
        private bool TickCore(float now, BarkContext context, bool tracing)
        {
            if (!_active) return false;
            if (Pending && !Valid(Version, now, context)) Cancel("NpcContextChanged", tracing);
            foreach (Probe p in _probes)
            {
                if (p.Player == null || now < p.Started + NpcReplyRules.ProbeDelay) continue;
                string reason = !Present(p.Player) ? "SpeakerUnavailable" : HearingReason(p);
                NpcRelation relation = Relation(p.Player, now);
                if (reason == null && !_fika.IsAI(p.Player))
                {
                    if (Pending && p.Started >= _target.HeardAt
                        && NpcDialogueRules.PeerAnswer(_response, PeerResponse(p.Phrase))
                        && Vector3.Distance(p.Player.Transform.position, _target.Player.Transform.position) <= NpcReplyRules.Range)
                    {
                        Cancel("NearbyPlayerReplied", tracing); _memory.OtherReplied(now);
                        foreach (Peer peer in _peers) peer.Open = false;
                    }
                }
                else if (_fika.IsAI(p.Player))
                {
                    if (reason == null)
                    {
                        Peer peer = Remember(p.Player, now);
                        if (peer == null) { reason = "NpcCapacity"; _capacityDrops++; }
                        else
                        {
                            // Keep an existing candidate's meaning and fixed deadline.
                            peer.HeardAt = p.Started;
                            if (peer == _target && Pending) { _memory.HeardAgain(p.Started + p.Clip.Length); reason = "ReplyAlreadyPending"; }
                            else
                            {
                                peer.Line = Meaning(p.Phrase); peer.Phrase = p.Phrase;
                                peer.End = p.Started + p.Clip.Length; peer.Expires = peer.End + 8f;
                                Vector3 delta = _local.Transform.position - peer.Player.Transform.position;
                                peer.Directed = delta.sqrMagnitude <= 225f && Math.Abs(delta.y) < 3f
                                    && (delta.sqrMagnitude <= 36f || Vector3.Dot(peer.Player.LookDirection, delta.normalized) >= 0.25f || relation == NpcRelation.Ally);
                                peer.LastReason = null; peer.Open = true;
                                reason = "AudibleNpcSpeech";
                            }
                        }
                    }
                    if (tracing) _trace(FormattableString.Invariant($"Event=NpcHeard SourceVoice={p.Player.Speaker?.PlayerVoice} Role={p.Player.Profile?.Info?.Settings?.Role} Relation={relation} HeardPhrase={p.Phrase} Meaning={Meaning(p.Phrase)} Duration={p.Clip.Length:F2} Decision={(reason == "AudibleNpcSpeech" ? "Observed" : "Suppressed")} Reason={reason} NpcEpisode={Version}"));
                }
                p.Clear();
            }
            Sample(now);
            Peer best = null; NpcResponse selected = NpcResponse.None;
            foreach (Peer peer in _peers)
            {
                if (!peer.Open) continue;
                if (now >= peer.Expires || !Available(peer.Player))
                { peer.Open = false; Decision(peer, "Suppressed", "NpcOpportunityExpired", tracing); continue; }
                NpcResponse response = Select(peer, now, context);
                if (response == NpcResponse.None)
                {
                    bool consumedConversation = peer.ConversationChecked
                        && NpcDialogueRules.EnemyConversation(peer.Line, Relation(peer.Player, now), context.Survival);
                    string rejected = consumedConversation ? "EnemyConversationConsumed"
                        : context.Survival ? "SurvivalContext" : peer.Line == NpcLine.Other ? "UnmappedNpcPhrase"
                        : peer.Line == NpcLine.Pain || peer.Line == NpcLine.Death || peer.Line == NpcLine.Acknowledgement ? "NoReplyToThisMeaning"
                        : !peer.Directed ? "NotAddressedNearby" : "AwaitMatchingContextOrAction";
                    if (peer.LastReason != rejected) { Decision(peer, "Waiting", rejected, tracing); peer.LastReason = rejected; }
                    if (consumedConversation || rejected == "UnmappedNpcPhrase" || rejected == "NoReplyToThisMeaning") peer.Open = false;
                    continue;
                }
                if (!peer.Turns.Available(now))
                { peer.Open = false; Decision(peer, "Suppressed", "NpcTurnLimit", tracing); continue; }
                if (best == null || NpcDialogueRules.Priority(response) > NpcDialogueRules.Priority(selected)
                    || NpcDialogueRules.Priority(response) == NpcDialogueRules.Priority(selected) && Distance(peer.Player) < Distance(best.Player))
                { best = peer; selected = response; }
            }
            if (tracing && _capacityDrops > 0 && now >= _capacityTraceAt)
            { _trace($"Event=NpcCapacity Dropped={_capacityDrops} Capacity={NpcDialogueRules.Capacity}"); _capacityDrops = 0; _capacityTraceAt = now + 30f; }
            if (best == null || Pending && NpcDialogueRules.Priority(selected) <= Priority) return false;
            if (selected == NpcResponse.EnemyConversation && !best.Conversation.Exists)
            {
                best.ConversationChecked = true;
                // Anchor validity at the end of the heard line so a long native
                // clip remains eligible through its normal response delay.
                best.Conversation = _enemyConversation.Hear(best.Player.ProfileId, 1, best.End,
                    true, true, true, true);
                // One NPC gets one exact opportunity. Capacity or duplicate
                // memory must remain silent instead of degrading to a generic
                // retort on a later tick.
                if (!best.Conversation.Exists)
                {
                    best.Open = false;
                    Decision(best, "Suppressed", "EnemyConversationConsumed", tracing);
                    return false;
                }
            }
            bool replace = Pending;
            if (replace) Cancel("HigherPriorityNpcSpeech", tracing, replace: true);
            string blocked = _memory.Begin(now, best.End, Distance(best.Player), (float)_random.NextDouble());
            best.Open = false;
            if (blocked != null) { Decision(best, "Suppressed", blocked, tracing); return false; }
            _target = best; _response = selected;
            Decision(best, "Candidate", selected.ToString(), tracing);
            return true;
        }
        private void Decision(Peer peer, string decision, string reason, bool tracing)
        {
            if (tracing) _trace($"Event=NpcDecision NpcEpisode={Version} HeardPhrase={peer.Phrase} Meaning={peer.Line} Decision={decision} Reason={reason} Turns={peer.Turns.Replies}");
        }
        private NpcResponse Select(Peer peer, float now, BarkContext context)
        {
            Vector3 delta = _local.Transform.position - peer.Player.Transform.position;
            float distance = delta.magnitude;
            bool directed = peer.Directed && distance <= 15f && Math.Abs(delta.y) < 3f;
            NpcRelation relation = Relation(peer.Player, now);
            if (NpcDialogueRules.EnemyConversation(peer.Line, relation, context.Survival))
            {
                if (!peer.ConversationChecked) return NpcResponse.EnemyConversation;
                return peer == _target && Pending && _enemyConversation.Valid(peer.Conversation, now)
                    ? NpcResponse.EnemyConversation : NpcResponse.None;
            }
            return NpcDialogueRules.Select(peer.Line, relation, context.Combat == CombatPhase.Engaged,
                context.Survival, directed, distance <= 6f, peer.Actions);
        }
        private static bool Stable(ref float since, bool fits, float now)
        {
            if (!fits) { since = -1; return false; }
            if (since < 0) since = now;
            return now - since >= 0.75f;
        }
        private void Sample(float now)
        {
            if (_sampled && now - _sampleAt < 0.25f) return;
            _samples++;
            Vector3 position = _local.Transform.position;
            float dt = now - _sampleAt;
            Vector3 move = position - _localPosition; move.y = 0f;
            bool fresh = _sampled && dt > 0f && dt <= 1f && move.sqrMagnitude < 100f;
            bool moving = fresh && move.magnitude / dt > 0.35f;
            foreach (Peer peer in _peers)
            {
                if (peer.Player == null || !Available(peer.Player) || now - peer.HeardAt > 12f) continue;
                Vector3 at = peer.Player.Transform.position, delta = at - position, other = at - peer.Position;
                float elapsed = now - peer.SampleAt;
                other.y = 0f;
                bool near = delta.sqrMagnitude <= 225f && Math.Abs(delta.y) < 3f;
                bool otherMoving = elapsed > 0 && elapsed <= 1f && other.magnitude / elapsed > 0.35f && other.sqrMagnitude < 100f;
                Vector3 facing = peer.Player.LookDirection; facing.y = 0f;
                peer.Actions = new NpcActions {
                    Following = Stable(ref peer.FollowSince, near && moving && otherMoving && Vector3.Dot(move.normalized, other.normalized) > 0.6f && Vector3.Dot(delta.normalized, other.normalized) > 0.2f, now),
                    Approaching = Stable(ref peer.ApproachSince, near && moving && delta.sqrMagnitude > 4f && Vector3.Dot(move.normalized, delta.normalized) > 0.6f, now),
                    Advancing = Stable(ref peer.AdvanceSince, near && moving && Vector3.Dot(move.normalized, facing.normalized) > 0.6f, now),
                    Retreating = Stable(ref peer.RetreatSince, near && moving && Vector3.Dot(move.normalized, facing.normalized) < -0.6f, now),
                    Holding = Stable(ref peer.HoldSince, near && fresh && !moving, now),
                    Covering = near && now < peer.CoverUntil
                };
                peer.Position = at; peer.SampleAt = now;
            }
            _localPosition = position; _sampleAt = now; _sampled = true;
        }
        public void Hit(Player victim, DamageInfo info, bool witnessed)
        {
            if (!_active || !witnessed || info.Damage <= 0 || !VoiceDirector.IsCombatDamage(info.DamageType)) return;
            IPlayer attacker = info.Player?.iPlayer;
            if (attacker == null || attacker.ProfileId == _local.ProfileId || ViewProbe.IsAlly(_local, attacker)) return;
            if (!Available(victim) || !_fika.IsAI(victim) || Distance(victim) > NpcReplyRules.Range
                || Relation(victim, Time.time) == NpcRelation.Hostile) return;
            // The hit can precede the first request for help. Reuse the director's
            // witnessed victim instead of requiring an earlier voice callback.
            Peer peer = Remember(victim, Time.time);
            if (peer != null) { peer.Attacker = attacker.ProfileId; peer.HitAt = Time.time; }
        }
        public void Shot(float now, VisibleContact target)
        {
            if (!_active || target == null || !target.Alive || target.Ally) return;
            foreach (Peer peer in _peers)
                if (peer.Player != null && now - peer.HitAt <= 8f && peer.Attacker == target.Id)
                    peer.CoverUntil = now + 3.5f;
        }
        private string HearingReason(Probe p)
        {
            if (p.Source == null || p.Source.source1 == null) return "AudioSourceGone";
            var audio = p.Source.source1;
            return NpcReplyRules.HearingReason(audio.isPlaying && audio.clip == p.Clip.Clip,
                p.Source.CanPlay && audio.volume > 0.001f && !AudioListener.pause && AudioListener.volume > 0f,
                Distance(p.Player), Math.Min(p.Clip.Falloff, p.Source.MaxDistance), p.Source.OcclusionRolloffScale,
                p.Clip.Volume, p.Source.OcclusionVolumeFactor, p.Source.FadeFactor);
        }
        public bool Valid(int version, float now, BarkContext context) => _active && _memory.Valid(version, now)
            && _target != null && Available(_target.Player) && _fika.IsAI(_target.Player) && Distance(_target.Player) <= NpcReplyRules.Range
            && (_response != NpcResponse.EnemyConversation || _enemyConversation.Valid(_target.Conversation, now))
            && Select(_target, now, context) == _response;
        public string Waiting(float now) => _memory.Waiting(now);
        public void Spoken(float now, float cooldown)
        {
            _target?.Turns.Spoken(now); _memory.Spoken(now, cooldown); _target = null;
            foreach (Peer peer in _peers) peer.Open = false;
        }
        public void Cancel(string reason, bool tracing, bool replace = false)
        {
            if (tracing && Pending) _trace($"Event=NpcReply Decision=Suppressed Reason={reason} NpcEpisode={Version}");
            if (replace) _memory.Supersede(); else _memory.Cancel();
            _target = null;
        }
        public void Pause()
        {
            _active = false; _memory.Cancel(); _target = null; _sampled = false;
            foreach (Probe p in _probes) p.Clear();
            foreach (Peer peer in _peers) { peer.Open = false; peer.Actions = default; }
        }
        internal static EPhraseTrigger Output(NpcResponse response)
            => response == NpcResponse.EnemyConversation ? EPhraseTrigger.OnEnemyConversation
                : response == NpcResponse.Retort ? EPhraseTrigger.OnFight : response == NpcResponse.Noise ? EPhraseTrigger.NoisePhrase
                : response == NpcResponse.Mutter ? EPhraseTrigger.OnMutter : response == NpcResponse.Following ? EPhraseTrigger.Going
                : response == NpcResponse.Covering ? EPhraseTrigger.Covering : EPhraseTrigger.Roger;
        private static NpcResponse PeerResponse(EPhraseTrigger phrase)
            => phrase == EPhraseTrigger.OnEnemyConversation ? NpcResponse.EnemyConversation
                : phrase == EPhraseTrigger.OnFight ? NpcResponse.Retort : phrase == EPhraseTrigger.NoisePhrase ? NpcResponse.Noise
                : phrase == EPhraseTrigger.OnMutter ? NpcResponse.Mutter : phrase == EPhraseTrigger.Going ? NpcResponse.Following
                : phrase == EPhraseTrigger.Covering ? NpcResponse.Covering : phrase == EPhraseTrigger.Roger ? NpcResponse.Acknowledge : NpcResponse.None;
        internal static NpcLine Meaning(EPhraseTrigger phrase)
        {
            switch (phrase)
            {
                case EPhraseTrigger.OnFight: case EPhraseTrigger.Toxic: case EPhraseTrigger.Provocation: case EPhraseTrigger.Rat: return NpcLine.Taunt;
                case EPhraseTrigger.OnFirstContact: case EPhraseTrigger.OnRepeatedContact: case EPhraseTrigger.OnEnemyConversation: return NpcLine.Combat;
                case EPhraseTrigger.OnMutter: return NpcLine.Mutter;
                case EPhraseTrigger.OnWeaponReload: case EPhraseTrigger.OnOutOfAmmo: case EPhraseTrigger.NeedAmmo:
                case EPhraseTrigger.OnWeaponJammed: case EPhraseTrigger.WeaponBroken: return NpcLine.Reload;
                case EPhraseTrigger.NeedHelp: case EPhraseTrigger.NeedMedkit: return NpcLine.Help;
                case EPhraseTrigger.FollowMe: return NpcLine.Follow;
                case EPhraseTrigger.CoverMe: return NpcLine.Cover;
                case EPhraseTrigger.GoForward: case EPhraseTrigger.Gogogo: return NpcLine.Advance;
                case EPhraseTrigger.GetBack: return NpcLine.Retreat;
                case EPhraseTrigger.Stop: case EPhraseTrigger.HoldPosition: return NpcLine.Hold;
                case EPhraseTrigger.Regroup: return NpcLine.Regroup;
                case EPhraseTrigger.Cooperation: return NpcLine.Cooperation;
                case EPhraseTrigger.OnEnemyGrenade: case EPhraseTrigger.OnGrenade: case EPhraseTrigger.Warning: return NpcLine.Warning;
                case EPhraseTrigger.OnBeingHurt: case EPhraseTrigger.OnAgony: case EPhraseTrigger.OnBeingHurtDissapoinment:
                case EPhraseTrigger.HurtLight: case EPhraseTrigger.HurtMedium: case EPhraseTrigger.HurtHeavy: case EPhraseTrigger.HurtNearDeath: return NpcLine.Pain;
                case EPhraseTrigger.OnDeath: return NpcLine.Death;
                case EPhraseTrigger.OnBreath: return NpcLine.Breath;
                case EPhraseTrigger.Roger: case EPhraseTrigger.Going: case EPhraseTrigger.Covering: case EPhraseTrigger.GoodWork: case EPhraseTrigger.OnGoodWork: return NpcLine.Acknowledgement;
                default: return NpcLine.Other;
            }
        }
    }
}
