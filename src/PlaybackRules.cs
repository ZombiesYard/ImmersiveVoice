namespace ImmersiveVoice
{
    internal sealed class PlaybackRecovery
    {
        public int Failures { get; private set; }
        public bool Silenced => Failures >= 3;
        public void Complete(bool faulted, bool confirmed)
        {
            if (faulted) Failures = System.Math.Min(3, Failures + 1);
            else if (confirmed) Failures = 0;
        }
    }

    internal static class PlaybackRules
    {
        public const float BreathGrace = 0.8f, LongBreath = 3f;
        public static bool EventVoice(VoiceCue cue) => cue != null && (cue.Urgent
            || VisualRules.TacticalReport(cue.Intent) || cue.Group == "health" || cue.Intent == VoiceIntent.Healing
            || cue.Intent == VoiceIntent.TeamReady || SemanticVoice.NpcExchange(cue.Intent) && cue.Intent != VoiceIntent.NpcMutter);
        public static bool YieldBreath(bool sameClip, bool breathing, float duration, float elapsed, VoiceCue cue)
            => sameClip && breathing && duration >= LongBreath && elapsed >= BreathGrace && EventVoice(cue);
        public static string Movement(VoiceIntent intent, bool urgent, bool aiming, bool sprinting, bool stealth)
        {
            if (urgent || VisualRules.TacticalReport(intent)) return null;
            return aiming && intent != VoiceIntent.TeamReady && intent != VoiceIntent.NpcRetort && intent != VoiceIntent.NpcCovering && intent != VoiceIntent.NpcNoise ? "Aiming" : sprinting ? "Sprinting" : stealth ? "Stealth" : null;
        }
    }
}
