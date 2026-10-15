using ImmersiveVoice;

internal static class PlaybackScenarios
{
    public static void Run(Action<bool, string> check)
    {
        var recovery = new PlaybackRecovery();
        recovery.Complete(true, false); recovery.Complete(true, false);
        check(!recovery.Silenced && recovery.Failures == 2, "isolated native audio failures do not disable the entire raid");
        recovery.Complete(false, false);
        check(recovery.Failures == 2, "busy or missing audio cannot erase unresolved native failures");
        recovery.Complete(false, true);
        check(recovery.Failures == 0, "a successful later phrase resets the native exception budget");
        for (int i = 0; i < 10; i++) recovery.Complete(true, true);
        check(recovery.Silenced && recovery.Failures == 3,
            "repeated native exceptions are bounded even when thrown after a speaker confirmation");
        check(!new PlaybackRecovery().Silenced, "a new raid starts with an independent exception budget");
        var contact = new VoiceCue { Key = "contact:a", Group = "contact", Intent = VoiceIntent.Contact };
        var health = new VoiceCue { Key = "health:a", Group = "health", Intent = VoiceIntent.NativeContext };
        var heal = new VoiceCue { Key = "heal", Group = "heal", Intent = VoiceIntent.Healing };
        var idle = new VoiceCue { Key = "idle", Group = "calm", Intent = VoiceIntent.CalmMutter };
        foreach (float duration in new[] { 6.65f, 24.01f })
        {
            var policy = new VoicePolicy();
            policy.Spoken(10, duration, 8, false, true, 12, null, breathing: true);
            check(!PlaybackRules.YieldBreath(true, true, duration, 0.79f, contact)
                && policy.BlockReason(contact, 10.79f) == "SpeechPlaying", "long breath retains its initial grace period");
            foreach (var cue in new[] { contact, health, heal })
            {
                check(PlaybackRules.YieldBreath(true, true, duration, 0.81f, cue)
                    && policy.BlockReason(cue, 10.81f) == null, "confirmed event can yield " + duration + " second breathing");
            }
            check(!PlaybackRules.YieldBreath(true, true, duration, 1, idle)
                && policy.BlockReason(idle, 11) == "SpeechPlaying", "ambient chatter cannot interrupt breathing");
            policy.Observe(health, 10, 0.3f);
            check(policy.CanUseContext(10.81f), "native health hints use the same bounded breath policy");
            policy.Spoken(11, 1, 8, true, false, 12, heal);
            check(policy.BlockReason(contact, 12.41f) == null, "interrupted long breathing leaves no stale speech reservation");
        }
        check(!PlaybackRules.YieldBreath(false, true, 24, 1, contact)
            && !PlaybackRules.YieldBreath(true, false, 24, 1, contact)
            && !PlaybackRules.YieldBreath(true, true, 1.5f, 1, contact),
            "unknown clips, pain, death and short breathing cannot use the long-breath exception");
        var p = new VoicePolicy();
        p.Spoken(10, 24.01f, 8, false, true, 12, null, breathing: true);
        p.Manual(10.5f, 12);
        check(p.BlockReason(contact, 11) == "ManualPriority", "long breathing never bypasses manual silence");
        p = new VoicePolicy();
        p.Spoken(10, 24.01f, 8, false, true, 12, null, breathing: true);
        p.Spoken(11, 2, 8, false, true, 12, null);
        check(p.BlockReason(contact, 12) == "SpeechPlaying", "a new short pain clip cancels breath eligibility");

        foreach (var intent in new[] { VoiceIntent.Contact, VoiceIntent.RepeatedContact, VoiceIntent.TacticalEnemyDown,
            VoiceIntent.LostVisual, VoiceIntent.TeamFollowing, VoiceIntent.TeamCovering, VoiceIntent.TeamAdvance,
            VoiceIntent.TeamRetreat, VoiceIntent.TeamLead, VoiceIntent.TeamNeedCover, VoiceIntent.TeamRegroup,
            VoiceIntent.TeamHold, VoiceIntent.Acknowledge, VoiceIntent.MedicalRequest, VoiceIntent.FriendlyDown })
        {
            p = new VoicePolicy(); p.Spoken(10, 1, 8, true, false, 12, idle);
            var cue = new VoiceCue { Intent = intent, Key = intent.ToString(), Group = "test" };
            check(p.BlockReason(cue, 11.41f) == null && PlaybackRules.Movement(intent, false, true, true, true) == null,
                intent + " is not starved by ordinary spacing, crouching, aiming or sprinting");
        }
        check(PlaybackRules.Movement(VoiceIntent.CalmMutter, false, false, false, true) == "Stealth"
            && PlaybackRules.Movement(VoiceIntent.ReloadAction, false, false, true, false) == "Sprinting"
            && PlaybackRules.Movement(VoiceIntent.KillTaunt, false, true, false, false) == "Aiming",
            "movement protection remains on ordinary action and emotional chatter");

        string reason = null;
        var queue = new ReactionQueue((_, why) => reason = why);
        queue.Offer(new Reaction { Cue = contact, ReadyAt = 10.3f, ExpiresAt = 14 }, 10);
        check(queue.Next(11, _ => true, _ => false) == null && queue.Count == 1,
            "native busy state waits without removing a valid pending report");
        check(queue.Next(14, _ => true, _ => false) == null && queue.Count == 0 && reason == "Expired",
            "queue expires on schedule even during a 24-second native clip");

        var c = new BarkContext { Injury = InjuryLevel.Injured, Bleeding = true };
        var memory = new HealthSpeechMemory(); memory.Observe(0, c);
        memory.Select(50);
        memory.Failed(); memory.Failed();
        check(!memory.Considered && memory.BlockReason(HealthCue.Bleeding, c, 5) == null,
            "two unconfirmed wound attempts do not consume the episode");
        check(memory.MatchesSelection(50) && !memory.MatchesSelection(53),
            "temporary failure permits retrying the exact phrase without switching to another health hint");
        memory.Consider();
        check(memory.Disposition == HealthDisposition.Expressed && memory.Considered,
            "a subsequent confirmed wound clip consumes the episode");
        memory.BeginEvent(); memory.Failed(); memory.Failed(); memory.Failed();
        check(memory.BlockReason(HealthCue.Bleeding, c, 7) == "HealthPlaybackRetryLimit",
            "three unconfirmed attempts bound retries without claiming the wound was spoken");
        memory.Pause(); memory.Observe(100, c);
        check(memory.Disposition == HealthDisposition.RetryLimit, "pausing does not reset a failed wound retry budget");
        memory.BeginEvent(); memory.Missing();
        check(memory.BlockReason(HealthCue.Injury, c, 101) == "SameHealthEpisode"
            && memory.Disposition == HealthDisposition.MissingAudio,
            "missing exact bleeding audio cannot be replaced by another health phrase");
        c.LegFracture = true; memory.Observe(102, c);
        check(!memory.Considered && memory.MatchesSelection(49), "a new actual fracture opens a new decision after missing audio");

        check(!MedicalRequestPolicy.Recipient(false, true, true, 0)
            && !MedicalRequestPolicy.Recipient(true, false, true, 0)
            && !MedicalRequestPolicy.Recipient(true, true, false, 0)
            && !MedicalRequestPolicy.Recipient(true, true, true, 1.01f)
            && MedicalRequestPolicy.Recipient(true, true, true, 1),
            "medical requests require an existing recent live human recipient even with generic nearby-team context");

        SquadPoint P(float x, float z = 0) => new(x, 0, z);
        SquadMemory Formation()
        {
            var m = new SquadMemory(); m.Bind(0);
            for (float t = 0; t <= 4; t += 0.25f) m.Sample(0, t, P(t * 2), P(t * 2 + 0.8f, 4), true);
            return m;
        }
        var squad = Formation(); var following = squad.Decide(4, true, true, true);
        check(following.Action == SquadAction.Following, "jitter regression begins with established following");
        squad.Sample(0, 4.25f, P(8.6f), P(9.3f, 4), true);
        check(squad.Valid(following, 4.25f, true), "small formation-threshold drift retains a selected line while both keep moving");
        squad.Sample(0, 4.75f, P(9.8f), P(10.3f, 4), true);
        check(!squad.Valid(following, 4.75f, true), "selected-line grace never becomes indefinite formation memory");
        squad = Formation(); following = squad.Decide(4, true, true, true);
        squad.Sample(0, 4.25f, P(8), P(8.7f, 4), true);
        check(!squad.Valid(following, 4.25f, true), "formation grace cannot revive an invalid formation after the local player stops");
        squad = Formation(); following = squad.Decide(4, true, true, true);
        squad.Sample(0, 4.25f, P(6.5f), P(7.3f, 4), true);
        check(!squad.Valid(following, 4.25f, true), "a reversal cancels the selected following statement");
    }
}
