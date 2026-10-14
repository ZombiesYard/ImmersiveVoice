namespace ImmersiveVoice
{
    internal static class PlaybackRules
    {
        public const float BreathGrace = 0.8f, LongBreath = 3f;
        public static bool EventVoice(VoiceCue cue) => cue != null && (cue.Urgent
            || VisualRules.TacticalReport(cue.Intent) || cue.Group == "health" || cue.Intent == VoiceIntent.Healing
            || cue.Intent == VoiceIntent.NpcRetort);
        public static bool YieldBreath(bool sameClip, bool breathing, float duration, float elapsed, VoiceCue cue)
            => sameClip && breathing && duration >= LongBreath && elapsed >= BreathGrace && EventVoice(cue);
        public static string Movement(VoiceIntent intent, bool urgent, bool aiming, bool sprinting, bool stealth)
        {
            if (urgent || VisualRules.TacticalReport(intent)) return null;
            return aiming && intent != VoiceIntent.NpcRetort ? "Aiming" : sprinting ? "Sprinting" : stealth ? "Stealth" : null;
        }
    }
}
