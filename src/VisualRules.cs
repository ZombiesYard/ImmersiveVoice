namespace ImmersiveVoice
{
    internal static class VisualRules
    {
        // Only classification of the object actually seen. Uncertain factions,
        // special Scavs and other human players still require direct hostility.
        public static bool RecognizableEnemy(bool ally, bool ai, bool localPmc, bool targetPmc, bool ordinaryScav)
            => !ally && ai && (targetPmc || (localPmc && ordinaryScav));
        public static bool TacticalReport(VoiceIntent intent) => intent == VoiceIntent.Contact
            || intent == VoiceIntent.RepeatedContact || intent == VoiceIntent.TacticalEnemyDown
            || intent == VoiceIntent.LostVisual || intent == VoiceIntent.FriendlyDown || intent == VoiceIntent.MedicalRequest
            || intent == VoiceIntent.Acknowledge || intent == VoiceIntent.TeamFollowing || intent == VoiceIntent.TeamCovering
            || intent == VoiceIntent.TeamAdvance || intent == VoiceIntent.TeamRetreat || intent == VoiceIntent.TeamLead
            || intent == VoiceIntent.TeamOnPosition || intent == VoiceIntent.TeamGoodWork
            || intent == VoiceIntent.TeamNeedCover || intent == VoiceIntent.TeamRegroup || intent == VoiceIntent.TeamHold;
    }
}
