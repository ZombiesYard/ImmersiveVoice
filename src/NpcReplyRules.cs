using System;

namespace ImmersiveVoice
{
    internal static class NpcReplyRules
    {
        public const float Range = 35f, ProbeDelay = 0.15f, ReplyWindow = 5f;
        public static bool Hostile(bool ally, bool knownHostile, bool localPmc, bool targetPmc, bool ordinaryScav)
            => !ally && (knownHostile || targetPmc || localPmc && ordinaryScav);

        // Reuse the native audio source's attenuation, without inventing sight
        // evidence or querying NPC brains. This is an audibility estimate.
        public static string HearingReason(bool playing, bool enabled, float distance, float falloff,
            float rangeScale, float volume, float occlusion, float fade)
        {
            if (!playing || !enabled) return "AudioNotPlaying";
            if (!Finite(distance) || distance < 0f || !Finite(falloff) || !Finite(rangeScale)
                || !Finite(volume) || !Finite(occlusion) || !Finite(fade)) return "InvalidAudioState";
            float range = Math.Min(Range, Math.Max(0f, falloff) * Clamp(rangeScale));
            if (range <= 0f || distance >= range) return "OutsideHearingRange";
            return volume * Clamp(occlusion) * Clamp(fade) * (1f - distance / range) < 0.02f
                ? "AudioTooQuiet" : null;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float Clamp(float value) => Math.Max(0f, Math.Min(1f, value));
    }

    // One outstanding exchange. Failed attempts do not consume the successful
    // reply cooldown; a short admission throttle still prevents callback storms.
    internal sealed class NpcReplyMemory
    {
        public int Version { get; private set; }
        public bool Pending { get; private set; }
        public float ReadyAt { get; private set; }
        public float ExpiresAt { get; private set; }
        public float SpeakingUntil { get; private set; }
        private float _considerAt, _replyAt, _allyUntil;

        public string Begin(float now, float speechEnd, float distance, float variation)
        {
            if (Pending) return "ReplyAlreadyPending";
            if (now < _replyAt) return "ReplyCooldown";
            if (now < _allyUntil) return "NearbyPlayerReplied";
            if (now < _considerAt) return "ObservationThrottle";
            _considerAt = now + 5f;
            Pending = true; Version++;
            SpeakingUntil = speechEnd;
            ReadyAt = Math.Max(now, speechEnd) + 0.65f + Math.Max(0f, Math.Min(1f, variation)) * 1.2f
                + Math.Max(0f, Math.Min(NpcReplyRules.Range, distance)) * 0.025f;
            ExpiresAt = ReadyAt + NpcReplyRules.ReplyWindow;
            return null;
        }
        public bool Valid(int version, float now) => Pending && Version == version && now < ExpiresAt;
        public void HeardAgain(float speechEnd) { if (Pending) SpeakingUntil = Math.Max(SpeakingUntil, speechEnd); }
        public string Waiting(float now) => now < SpeakingUntil + 0.35f ? "NpcStillSpeaking" : null;
        public void OtherReplied(float now) { _allyUntil = now + 3f; Cancel(); }
        public void Spoken(float now, float cooldown) { _replyAt = now + cooldown; Cancel(); }
        public void Cancel() { Pending = false; Version++; }
    }
}
