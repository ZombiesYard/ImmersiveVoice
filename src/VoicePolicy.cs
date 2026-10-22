using System;
using System.Collections.Generic;

namespace ImmersiveVoice
{
    // Pure policy: no Unity clock, random source, or game state. The adapter only
    // supplies cues the local game already exposes to the human player.
    internal sealed class VoiceCue
    {
        public int RequestId;
        public DialogueTicket Dialogue;
        public int Phrase;
        public string Key;
        public string Group;
        public bool Urgent;
        public float Repeat;
        public float GroupGap;
        public VoiceIntent Intent;
        public float SemanticRepeat;
        public bool AcuteHealth, HeavyBleeding;
        public InjuryLevel HealthSeverity;
    }

    internal sealed class VoicePolicy
    {
        private readonly Dictionary<string, float> _keys = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _groups = new Dictionary<string, float>();
        private readonly Dictionary<VoiceIntent, float> _intents = new Dictionary<VoiceIntent, float>();
        private VoiceCue _candidate;
        private float _candidateAt;
        private float _retryAt;
        private float _speechEnd;
        private float _breathYieldAt = float.MaxValue;
        private float _normalAt;
        private float _manualAt;
        private float _nextPrune;
        private InjuryLevel _healthSeverity;
        private bool _healthHeavyBleeding;

        public void Observe(VoiceCue cue, float now, float reaction)
        {
            if (cue == null)
            {
                _candidate = null;
                return;
            }
            if (_candidate == null || _candidate.Key != cue.Key || _candidate.Phrase != cue.Phrase)
                _candidateAt = now + reaction;
            _candidate = cue;
            if (now >= _nextPrune)
            {
                Prune(_keys, now);
                Prune(_groups, now);
                _nextPrune = now + 60f;
            }
        }

        public bool CanSpeak(float now, bool urgent = false)
        {
            return now >= _retryAt && now >= _manualAt && now >= _speechEnd + 0.4f
                && (urgent || now >= _normalAt);
        }

        public bool CanUseContext(float now)
        {
            return _candidate != null && now >= _candidateAt && BlockReason(_candidate, now) == null;
        }

        public bool CanUse(VoiceCue cue, float now)
        {
            return BlockReason(cue, now) == null;
        }

        public string BlockReason(VoiceCue cue, float now)
        {
            if (now < _manualAt) return "ManualPriority";
            float speechEnd = PlaybackRules.EventVoice(cue) ? Math.Min(_speechEnd, _breathYieldAt) : _speechEnd;
            if (now < speechEnd + 0.4f) return "SpeechPlaying";
            if (now < _retryAt) return "PlaybackBackoff";
            if (!cue.Urgent && !VisualRules.TacticalReport(cue.Intent) && !SemanticVoice.NpcExchange(cue.Intent)
                && cue.Intent != VoiceIntent.TeamReady && cue.Intent != VoiceIntent.TreatmentReady
                && cue.Intent != VoiceIntent.SearchLoot
                && now < _normalAt) return "GlobalGap";
            bool escalation = HealthEscalation(cue, now);
            if (!escalation && !Ready(_keys, cue.Key, now)) return "RecentSubject";
            if (!escalation && !Ready(_groups, cue.Group, now)) return "RecentCategory";
            if (cue.SemanticRepeat > 0f && _intents.TryGetValue(SemanticVoice.HistoryKey(cue.Intent), out float until)
                && now < until) return "RecentSemanticIntent";
            return null;
        }

        // Only an acute, objectively worse wound can supersede a recent health
        // report. New hit callbacks, other fractures and passive hints cannot.
        public bool HealthEscalation(VoiceCue cue, float now) => cue != null && cue.AcuteHealth && cue.Group == "health"
            && !Ready(_groups, "health", now)
            && (cue.HealthSeverity > _healthSeverity
                || cue.HeavyBleeding && !_healthHeavyBleeding && cue.Intent == VoiceIntent.Bleeding);

        public bool ContextPending(float now)
        {
            // Give a fresh Y cue its reaction window before ambient speech.
            return _candidate != null && now < _candidateAt + 2f
                && Ready(_keys, _candidate.Key, now) && Ready(_groups, _candidate.Group, now);
        }

        public void Attempt(float now)
        {
            // A native bank can reject a phrase (missing tag, native speaker
            // limits, or another mod). Do not retry at the 10 Hz polling rate.
            _retryAt = now + 2f;
        }

        public void Spoken(float now, float duration, float gap, bool automatic, bool physiological,
            float manualPause, VoiceCue cue, bool breathing = false)
        {
            // Native playback replaces the speaker's current clip. An interrupted
            // 24-second breath must not leave a stale 24-second speech reservation.
            _speechEnd = now + Math.Max(0f, duration);
            _breathYieldAt = breathing && duration >= PlaybackRules.LongBreath
                ? now + PlaybackRules.BreathGrace - 0.4f : float.MaxValue;
            if (!physiological)
            {
                _normalAt = Math.Max(_normalAt, _speechEnd + gap);
                if (!automatic) Manual(now, duration + manualPause);
                if (cue != null)
                {
                    if (cue.Group == "health")
                    {
                        bool continuing = !Ready(_groups, "health", now);
                        _healthSeverity = continuing && _healthSeverity > cue.HealthSeverity ? _healthSeverity : cue.HealthSeverity;
                        _healthHeavyBleeding = cue.HeavyBleeding || continuing && _healthHeavyBleeding;
                    }
                    _keys[cue.Key] = now + cue.Repeat;
                    _groups[cue.Group] = now + cue.GroupGap;
                    if (cue.SemanticRepeat > 0f)
                        _intents[SemanticVoice.HistoryKey(cue.Intent)] = now + cue.SemanticRepeat;
                }
            }
        }

        public void Manual(float now, float pause)
        {
            _manualAt = Math.Max(_manualAt, now + pause);
        }

        private static bool Ready(Dictionary<string, float> timers, string key, float now)
        {
            return !timers.TryGetValue(key, out float time) || now >= time;
        }

        private static void Prune(Dictionary<string, float> timers, float now)
        {
            var expired = new List<string>();
            foreach (var entry in timers)
                if (entry.Value <= now) expired.Add(entry.Key);
            foreach (string key in expired) timers.Remove(key);
        }
    }

    internal static class RuntimeRules
    {
        public static bool IsLocalRaid(bool headless, bool hideout, bool ownPlayer, bool ai,
            bool sameWorldPlayer, bool active, bool alive, bool downed, bool started, bool extracted)
        {
            return !headless && !hideout && ownPlayer && !ai && sameWorldPlayer
                && active && alive && !downed && started && !extracted;
        }
    }
}
