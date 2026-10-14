using ImmersiveVoice;

internal static class NpcReplyScenarios
{
    public static void Run(Action<bool, string> check)
    {
        check(!NpcReplyRules.Hostile(true, true, true, true, true), "NPC allies never become retort targets");
        check(!NpcReplyRules.Hostile(false, false, false, false, true), "Player Scav does not insult peaceful ordinary Scavs");
        check(NpcReplyRules.Hostile(false, true, false, false, true), "a Scav can reply to an actual known attacker");
        check(NpcReplyRules.Hostile(false, false, true, false, true), "PMC can respond to an audible ordinary Scav challenge");
        check(!NpcReplyRules.Hostile(false, false, true, false, false), "uncertain special NPCs need hostility evidence");
        check(NpcReplyRules.HearingReason(true, true, 12, 60, 1, 1, 0.3f, 1) == null,
            "native occlusion can permit hearing a nearby voice without line of sight");
        check(NpcReplyRules.HearingReason(true, true, 12, 60, 1, 1, 0, 1) == "AudioTooQuiet",
            "native full attenuation cannot produce a heard event");
        check(NpcReplyRules.HearingReason(true, true, 20, 60, 0.2f, 1, 1, 1) == "OutsideHearingRange",
            "occlusion-reduced range is respected");
        check(NpcReplyRules.HearingReason(true, true, 36, 100, 1, 1, 1, 1) == "OutsideHearingRange",
            "long-range voice assets cannot turn into whole-map replies");
        check(NpcReplyRules.HearingReason(false, true, 1, 60, 1, 1, 1, 1) == "AudioNotPlaying"
            && NpcReplyRules.HearingReason(true, false, 1, 60, 1, 1, 1, 1) == "AudioNotPlaying",
            "stopped or muted sources cannot trigger responses");
        check(NpcReplyRules.HearingReason(true, true, 1, 60, 1, float.NaN, 1, 1) == "InvalidAudioState",
            "invalid audio metadata fails closed");
        var memory = new NpcReplyMemory();
        check(memory.Begin(10, 13, 12, 0.5f) == null && memory.ReadyAt > 13.6f,
            "retort waits for the NPC line to finish plus a human response delay");
        int first = memory.Version; float deadline = memory.ExpiresAt;
        check(memory.Begin(11, 15, 10, 0) == "ReplyAlreadyPending" && memory.ExpiresAt == deadline,
            "repeated NPC callbacks cannot stack replies or extend their deadline");
        memory.HeardAgain(18);
        check(memory.Waiting(17) == "NpcStillSpeaking" && memory.ExpiresAt == deadline,
            "a further NPC line blocks overlap without prolonging the exchange");
        memory.OtherReplied(14);
        check(!memory.Valid(first, 14) && memory.Begin(16, 18, 10, 0) == "NearbyPlayerReplied",
            "an audible teammate reply cancels this client's pending exchange");
        check(memory.Begin(18, 20, 10, 0) == null, "a later independent exchange can start after another player's reply");
        memory.Spoken(22, 30);
        check(memory.Begin(40, 43, 10, 0) == "ReplyCooldown" && memory.Begin(52, 54, 10, 0) == null,
            "successful replies have a per-player cooldown across different NPCs");
        int second = memory.Version;
        memory.Cancel();
        check(!memory.Valid(second, 53) && memory.Begin(53, 55, 10, 0) == "ObservationThrottle"
            && memory.Begin(58, 60, 10, 0) == null,
            "canceled or missing-audio replies get only a short admission throttle, not a successful-play cooldown");
        check(!memory.Valid(memory.Version, memory.ExpiresAt), "expired replies never play long after the NPC spoke");

        var context = new BarkContext { Injury = InjuryLevel.Healthy };
        check(SemanticVoice.StillValid(VoiceIntent.NpcRetort, context, 0), "an audible NPC retort does not require firing first");
        context.Healing = true;
        check(!SemanticVoice.StillValid(VoiceIntent.NpcRetort, context, 0), "treatment suppresses bravado");
        context.Healing = false; context.RecentlyHit = true;
        check(!SemanticVoice.StillValid(VoiceIntent.NpcRetort, context, 0), "fresh damage cancels delayed insults");
        context.RecentlyHit = false; context.Injury = InjuryLevel.BadlyInjured;
        check(!SemanticVoice.StillValid(VoiceIntent.NpcRetort, context, 0), "severe injury takes priority over NPC replies");
        check(PlaybackRules.Movement(VoiceIntent.NpcRetort, false, true, false, false) == null
            && PlaybackRules.Movement(VoiceIntent.NpcRetort, false, false, false, true) == "Stealth",
            "aiming alone does not prevent a retort, but quiet movement still does");
        var cue = new VoiceCue { Intent = VoiceIntent.NpcRetort, Key = "npc", Group = "npc-reply", SemanticRepeat = 18 };
        check(PlaybackRules.YieldBreath(true, true, 24, 1, cue), "heard NPC exchanges can yield a long breath without interrupting speech");
        var policy = new VoicePolicy();
        policy.Spoken(10, 2, 8, true, false, 12,
            new VoiceCue { Intent = VoiceIntent.Contact, Key = "contact", Group = "contact" });
        check(policy.BlockReason(cue, 11) == "SpeechPlaying" && policy.BlockReason(cue, 13) == null,
            "a retort respects current speech but does not miss its short window because of the ordinary eight-second gap");
        policy.Spoken(13, 1, 8, true, false, 12, cue);
        check(policy.BlockReason(new VoiceCue { Intent = VoiceIntent.CombatBark, Key = "fight", Group = "combat", SemanticRepeat = 18 }, 23)
            == "RecentSemanticIntent", "NPC replies share repetition memory with combat taunts");
        policy.Manual(25, 12);
        check(policy.BlockReason(cue, 26) == "ManualPriority", "manual speech always suppresses an automatic NPC retort");

        check(NpcDialogueRules.Relationship(false, false, false, false, true) == NpcRelation.Neutral
            && NpcDialogueRules.Relationship(false, true, false, false, true) == NpcRelation.Hostile,
            "a peaceful Scav becomes a hostile conversational target after an observed attack");
        check(NpcDialogueRules.Relationship(false, false, true, false, false) == NpcRelation.Unknown,
            "a special NPC is not declared hostile merely by its role");
        NpcResponse Reply(NpcLine line, NpcRelation relation, bool engaged = false, bool directed = true,
            bool close = true, NpcActions actions = default)
            => NpcDialogueRules.Select(line, relation, engaged, false, directed, close, actions);
        check(Reply(NpcLine.Taunt, NpcRelation.Unknown) == NpcResponse.Retort,
            "an audible nearby directed challenge from a special NPC can receive a retort");
        check(Reply(NpcLine.Taunt, NpcRelation.Unknown, directed: false) == NpcResponse.Noise,
            "an uncertain distant speaker produces a noise observation instead of a direct challenge");
        check(Reply(NpcLine.Taunt, NpcRelation.Neutral) == NpcResponse.None
            && Reply(NpcLine.Combat, NpcRelation.Ally) == NpcResponse.None,
            "friendly combat shouts do not turn into insults or false acknowledgements");
        check(Reply(NpcLine.Mutter, NpcRelation.Hostile) == NpcResponse.Noise
            && Reply(NpcLine.Mutter, NpcRelation.Hostile, engaged: true) == NpcResponse.Retort,
            "overheard enemy chatter and active combat have different meanings");
        check(Reply(NpcLine.Reload, NpcRelation.Hostile, engaged: true) == NpcResponse.Retort
            && Reply(NpcLine.Help, NpcRelation.Hostile, engaged: true) == NpcResponse.Retort,
            "enemy reload and help speech can motivate a combat response without inventing a reload callout");
        check(Reply(NpcLine.Mutter, NpcRelation.Neutral) == NpcResponse.Mutter
            && Reply(NpcLine.Mutter, NpcRelation.Neutral, engaged: true) == NpcResponse.None
            && Reply(NpcLine.Mutter, NpcRelation.Neutral, close: false) == NpcResponse.None,
            "neutral muttering is limited to a close calm exchange");
        check(Reply(NpcLine.Follow, NpcRelation.Ally) == NpcResponse.None
            && Reply(NpcLine.Follow, NpcRelation.Ally, actions: new NpcActions { Following = true }) == NpcResponse.Following,
            "a friendly follow request waits for sustained matching motion");
        check(Reply(NpcLine.Cover, NpcRelation.Neutral) == NpcResponse.None
            && Reply(NpcLine.Cover, NpcRelation.Neutral, actions: new NpcActions { Covering = true }) == NpcResponse.Covering,
            "cover replies require firing at the witnessed attacker, not merely hearing a request");
        check(Reply(NpcLine.Advance, NpcRelation.Ally, actions: new NpcActions { Advancing = true }) == NpcResponse.Acknowledge
            && Reply(NpcLine.Retreat, NpcRelation.Ally, actions: new NpcActions { Retreating = true }) == NpcResponse.Acknowledge,
            "advance and retreat requests acknowledge matching movement");
        check(Reply(NpcLine.Hold, NpcRelation.Ally, actions: new NpcActions { Holding = true }) == NpcResponse.Acknowledge
            && Reply(NpcLine.Regroup, NpcRelation.Neutral, actions: new NpcActions { Approaching = true }) == NpcResponse.Acknowledge,
            "holding and regrouping can respond after observable action");
        check(Reply(NpcLine.Cooperation, NpcRelation.Neutral, actions: new NpcActions { Holding = true }) == NpcResponse.Acknowledge
            && Reply(NpcLine.Cooperation, NpcRelation.Neutral, close: false, actions: new NpcActions { Holding = true }) == NpcResponse.None,
            "a neutral cooperation request needs nearby presence or an approach");
        check(Reply(NpcLine.Warning, NpcRelation.Ally) == NpcResponse.Acknowledge,
            "hearing a friendly warning acknowledges it without inventing visual grenade evidence");
        foreach (NpcLine line in Enum.GetValues(typeof(NpcLine)))
        foreach (NpcRelation relation in Enum.GetValues(typeof(NpcRelation)))
            check(NpcDialogueRules.Select(line, relation, true, true, true, true,
                new NpcActions { Following = true, Covering = true }) == NpcResponse.None,
                "survival suppresses NPC dialogue: " + line + "/" + relation);
        foreach (var line in new[] { NpcLine.Breath, NpcLine.Pain, NpcLine.Death, NpcLine.Other, NpcLine.Acknowledgement })
        foreach (NpcRelation relation in Enum.GetValues(typeof(NpcRelation)))
            check(Reply(line, relation, engaged: true) == NpcResponse.None,
                "non-dialogue or answer-only speech cannot start a reply loop: " + line + "/" + relation);
        var turns = new NpcTurnMemory();
        turns.Spoken(10); turns.Spoken(40);
        check(!turns.Available(80) && turns.Replies == 2 && turns.Available(100),
            "two successful replies close a fixed ninety-second NPC window");
        turns.Spoken(100);
        check(turns.Replies == 1 && turns.Available(101), "a new encounter window renews only after the old window ends");
        check(NpcDialogueRules.PeerAnswer(NpcResponse.Covering, NpcResponse.Covering)
            && !NpcDialogueRules.PeerAnswer(NpcResponse.Covering, NpcResponse.Retort),
            "unrelated teammate taunts cannot swallow a friendly covering response");
        check(NpcDialogueRules.Priority(NpcResponse.Covering) > NpcDialogueRules.Priority(NpcResponse.Mutter)
            && NpcDialogueRules.Priority(NpcResponse.Retort) > NpcDialogueRules.Priority(NpcResponse.Mutter),
            "combat or coordination replies can supersede a pending calm mutter");
        var replacement = new NpcReplyMemory(); replacement.Begin(10, 12, 1, 0); replacement.Supersede();
        check(replacement.Begin(11, 13, 1, 0) == null, "higher-priority dialogue is not lost to its replaced candidate's admission throttle");
        context = new BarkContext { Injury = InjuryLevel.Healthy };
        foreach (var intent in new[] { VoiceIntent.NpcNoise, VoiceIntent.NpcMutter, VoiceIntent.NpcFollowing, VoiceIntent.NpcCovering, VoiceIntent.NpcAcknowledge })
        {
            context.Healing = false;
            check(SemanticVoice.StillValid(intent, context, 0), "healthy dialogue allowed: " + intent);
            context.Healing = true;
            check(!SemanticVoice.StillValid(intent, context, 0), "treatment cancels all delayed NPC meanings: " + intent);
        }
        check(SemanticVoice.HistoryKey(VoiceIntent.NpcFollowing) == VoiceIntent.TeamFollowing
            && SemanticVoice.HistoryKey(VoiceIntent.NpcMutter) == VoiceIntent.CalmMutter,
            "NPC output shares semantic history with existing equivalent player speech");
    }
}
