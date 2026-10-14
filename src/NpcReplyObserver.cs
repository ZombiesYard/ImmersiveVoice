using System;
using EFT;
using UnityEngine;

namespace ImmersiveVoice
{
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
        private static readonly EPhraseTrigger[] Inputs = { EPhraseTrigger.OnFight, EPhraseTrigger.OnEnemyConversation,
            EPhraseTrigger.OnFirstContact, EPhraseTrigger.OnRepeatedContact, EPhraseTrigger.Provocation, EPhraseTrigger.OnMutter };
        private readonly Probe[] _probes = { new Probe(), new Probe(), new Probe(), new Probe() };
        private readonly Player _local;
        private readonly FikaBridge _fika;
        private readonly Func<string, float, bool> _knownHostile;
        private readonly Action<string> _trace;
        private readonly NpcReplyMemory _memory = new NpcReplyMemory();
        private readonly System.Random _random = new System.Random();
        private Player _target;
        private bool _active;
        public int Version => _memory.Version;
        public float ReadyAt => _memory.ReadyAt;
        public float ExpiresAt => _memory.ExpiresAt;
        public bool Pending => _memory.Pending;

        public NpcReplyObserver(Player local, FikaBridge fika, Func<string, float, bool> knownHostile, Action<string> trace)
        { _local = local; _fika = fika; _knownHostile = knownHostile; _trace = trace; }

        public void Active(bool active) { if (!active) Pause(); else _active = true; }
        private bool Available(Player player) => player != null && player != _local && !player.IsYourPlayer
            && player.GameWorld == _local.GameWorld && player.gameObject.activeInHierarchy
            && player.HealthController?.IsAlive == true && !_fika.Downed(player);
        private float Distance(Player player) => Vector3.Distance(_local.Transform.position, player.Transform.position);
        private bool Hostile(Player player, float now)
        {
            WildSpawnType? role = player.Profile?.Info?.Settings?.Role;
            bool scav = role == WildSpawnType.assault || role == WildSpawnType.cursedAssault || role == WildSpawnType.marksman;
            return NpcReplyRules.Hostile(ViewProbe.IsAlly(_local, player), _knownHostile(player.ProfileId, now),
                _local.Side == EPlayerSide.Usec || _local.Side == EPlayerSide.Bear,
                player.Side == EPlayerSide.Usec || player.Side == EPlayerSide.Bear, scav);
        }
        // Each candidate bank is indexed once by NetId. No player roster, bank
        // scan, audio decoding, sound simulation or raycast is needed.
        private static EPhraseTrigger Identify(BaseSpeaker speaker, TaggedClip clip)
        {
            if (speaker == null || clip == null || clip.NetId < 0) return EPhraseTrigger.None;
            foreach (EPhraseTrigger phrase in Inputs)
                if (speaker.PhrasesBanks.TryGetValue(phrase, out var bank) && bank?.Clips != null
                    && clip.NetId < bank.Clips.Length && ReferenceEquals(bank.Clips[clip.NetId], clip)) return phrase;
            return EPhraseTrigger.None;
        }
        public void Capture(Player player, TaggedClip clip, BetterSource source)
        {
            if (!_active || !Available(player) || clip?.Clip == null || source == null
                || !(clip.Length > NpcReplyRules.ProbeDelay) || !(clip.Length <= 30f)
                || Distance(player) > NpcReplyRules.Range) return;
            bool ally = ViewProbe.IsAlly(_local, player);
            if (!player.IsAI && !ally || player.IsAI && ally) return;
            EPhraseTrigger phrase = Identify(player.Speaker, clip);
            if (phrase == EPhraseTrigger.None || !player.IsAI && phrase != EPhraseTrigger.OnFight) return;
            if (_memory.Pending && player.IsAI && player != _target) return;
            int free = -1;
            for (int i = 0; i < _probes.Length; i++)
            {
                if (_probes[i].Player == player) return;
                if (_probes[i].Player == null && free < 0) free = i;
            }
            if (free < 0) return;
            Probe probe = _probes[free];
            probe.Player = player; probe.Clip = clip; probe.Source = source; probe.Phrase = phrase; probe.Started = Time.time;
        }

        // Return true exactly once for a newly heard NPC exchange.
        public bool Tick(float now, BarkContext context, bool tracing)
        {
            if (!_active) return false;
            if (_memory.Pending && !Valid(Version, now)) Cancel("NpcNoLongerAvailable", tracing);
            bool offered = false;
            foreach (Probe p in _probes)
            {
                if (p.Player == null || now < p.Started + NpcReplyRules.ProbeDelay) continue;
                string reason = !Available(p.Player) ? "SpeakerUnavailable" : HearingReason(p);
                bool npc = p.Player.IsAI;
                if (reason == null && !npc)
                {
                    bool pending = _memory.Pending;
                    _memory.OtherReplied(now); _target = null;
                    if (pending && tracing) _trace("Event=NpcReply Decision=Suppressed Reason=NearbyPlayerReplied");
                }
                else if (reason == null)
                {
                    reason = !Hostile(p.Player, now) ? "NoHostileEvidence"
                        : p.Phrase == EPhraseTrigger.OnMutter && !_knownHostile(p.Player.ProfileId, now) ? "UnawareMutter"
                        : context.Survival ? "SurvivalContext" : null;
                    if (reason == null)
                    {
                        if (_memory.Pending && p.Player == _target) { _memory.HeardAgain(p.Started + p.Clip.Length); reason = "ReplyAlreadyPending"; }
                        else
                        {
                            reason = _memory.Begin(now, p.Started + p.Clip.Length, Distance(p.Player), (float)_random.NextDouble());
                            if (reason == null) { _target = p.Player; offered = true; }
                        }
                    }
                }
                if (npc && tracing) _trace(FormattableString.Invariant($"Event=NpcHeard SourceVoice={p.Player.Speaker?.PlayerVoice} HeardPhrase={p.Phrase} Duration={p.Clip.Length:F2} Decision={(reason == null ? "Candidate" : "Suppressed")} Reason={reason ?? "AudibleHostileSpeech"} NpcEpisode={Version}"));
                p.Clear();
            }
            return offered && _memory.Pending;
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
        public bool Valid(int version, float now) => _active && _memory.Valid(version, now) && Available(_target)
            && _target.IsAI && Hostile(_target, now) && Distance(_target) <= NpcReplyRules.Range;
        public string Waiting(float now) => _memory.Waiting(now);
        public void Spoken(float now, float cooldown) { _memory.Spoken(now, cooldown); _target = null; }
        public void Cancel(string reason, bool tracing)
        {
            if (tracing && _memory.Pending) _trace($"Event=NpcReply Decision=Suppressed Reason={reason} NpcEpisode={Version}");
            _memory.Cancel(); _target = null;
        }
        public void Pause()
        {
            _active = false; _memory.Cancel(); _target = null;
            foreach (Probe p in _probes) p.Clear();
        }
    }
}
