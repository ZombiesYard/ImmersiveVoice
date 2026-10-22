using System;

namespace ImmersiveVoice
{
    internal enum NpcRelation { Unknown, Neutral, Ally, Hostile }
    internal enum NpcLine { Other, Taunt, Combat, Mutter, Reload, Help, Follow, Cover, Advance, Retreat, Hold, Regroup, Cooperation, Warning, Pain, Death, Breath, Acknowledgement }
    internal enum NpcResponse { None, Retort, Noise, Mutter, Acknowledge, Following, Covering, EnemyConversation }

    internal struct NpcActions
    {
        public bool Following, Approaching, Advancing, Retreating, Holding, Covering;
    }

    internal static class NpcDialogueRules
    {
        public const int Capacity = 8;
        public static NpcRelation Relationship(bool ally, bool hostile, bool localPmc, bool targetPmc, bool ordinaryScav)
            => ally ? NpcRelation.Ally : hostile || targetPmc || localPmc && ordinaryScav ? NpcRelation.Hostile
                : !localPmc && ordinaryScav ? NpcRelation.Neutral : NpcRelation.Unknown;

        public static NpcResponse Select(NpcLine line, NpcRelation relation, bool engaged, bool survival,
            bool directed, bool close, NpcActions actions)
        {
            if (survival) return NpcResponse.None;
            bool friendly = relation == NpcRelation.Ally || relation == NpcRelation.Neutral;
            if (line == NpcLine.Other || line == NpcLine.Breath || line == NpcLine.Pain
                || line == NpcLine.Death || line == NpcLine.Acknowledgement) return NpcResponse.None;
            if (relation == NpcRelation.Hostile)
            {
                if (line == NpcLine.Taunt || line == NpcLine.Combat) return NpcResponse.Retort;
                if (line == NpcLine.Mutter) return engaged ? NpcResponse.Retort : NpcResponse.Noise;
                return engaged && (line == NpcLine.Reload || line == NpcLine.Help || line == NpcLine.Cover)
                    ? NpcResponse.Retort : NpcResponse.Noise;
            }
            if (relation == NpcRelation.Unknown)
                return line == NpcLine.Taunt && directed ? NpcResponse.Retort : NpcResponse.Noise;
            if (!friendly || !directed) return NpcResponse.None;
            switch (line)
            {
                // A calm mutter is an ambient response, never a fabricated greeting.
                case NpcLine.Mutter: return !engaged && close ? NpcResponse.Mutter : NpcResponse.None;
                case NpcLine.Follow: return actions.Following ? NpcResponse.Following : NpcResponse.None;
                case NpcLine.Cover: case NpcLine.Help: return actions.Covering ? NpcResponse.Covering : NpcResponse.None;
                case NpcLine.Advance: return actions.Advancing ? NpcResponse.Acknowledge : NpcResponse.None;
                case NpcLine.Retreat: return actions.Retreating ? NpcResponse.Acknowledge : NpcResponse.None;
                case NpcLine.Hold: return actions.Holding ? NpcResponse.Acknowledge : NpcResponse.None;
                case NpcLine.Regroup: case NpcLine.Cooperation:
                    return actions.Approaching || close && actions.Holding ? NpcResponse.Acknowledge : NpcResponse.None;
                case NpcLine.Warning: return NpcResponse.Acknowledge;
                default: return NpcResponse.None;
            }
        }
        public static bool EnemyConversation(NpcLine line, NpcRelation relation, bool survival)
            => !survival && relation == NpcRelation.Hostile
                && (line == NpcLine.Taunt || line == NpcLine.Combat || line == NpcLine.Mutter);
        public static VoiceIntent Intent(NpcResponse response)
            => response == NpcResponse.EnemyConversation ? VoiceIntent.NpcEnemyConversation
                : response == NpcResponse.Retort ? VoiceIntent.NpcRetort
                : response == NpcResponse.Noise ? VoiceIntent.NpcNoise
                : response == NpcResponse.Mutter ? VoiceIntent.NpcMutter
                : response == NpcResponse.Following ? VoiceIntent.NpcFollowing
                : response == NpcResponse.Covering ? VoiceIntent.NpcCovering : VoiceIntent.NpcAcknowledge;
        public static int Priority(NpcResponse response) => response == NpcResponse.EnemyConversation ? 60
            : response == NpcResponse.Noise ? 50
            : response == NpcResponse.Covering ? 58 : response == NpcResponse.Mutter ? 20 : 40;
        public static bool PeerAnswer(NpcResponse response, NpcResponse heard)
            => response != NpcResponse.None && response == heard;
    }

    // Successful lines alone consume turns. A busy or missing bank cannot exhaust
    // an exchange, and fresh callbacks cannot reset its fixed duration.
    internal sealed class NpcTurnMemory
    {
        public int Replies { get; private set; }
        private float _until;
        public bool Available(float now) => now >= _until || Replies < 2;
        public void Spoken(float now)
        {
            if (now >= _until) { Replies = 0; _until = now + 90f; }
            Replies++;
        }
        public void Clear() { Replies = 0; _until = 0; }
    }

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
        public void Supersede() { Cancel(); _considerAt = 0; }
        public void Cancel() { Pending = false; Version++; }
    }
}
