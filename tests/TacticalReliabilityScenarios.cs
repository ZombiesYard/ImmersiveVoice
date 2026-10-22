using ImmersiveVoice;

internal static class TacticalReliabilityScenarios
{
    private static SquadPoint P(float x, float z = 0) => new(x, 0, z);
    public static void Run(Action<bool, string> check)
    {
        var contacts = new ContactMemory();
        contacts.Hostile("a", 10); contacts.Observe("a", true, false, 10);
        check(contacts.Observe("a", true, false, 10.8f) == ReactionKind.Contact,
            "a four-target round-robin cadence still establishes contact with two real sightings");
        contacts.Hostile("b", 11); contacts.Observe("b", true, false, 11);
        check(contacts.ContactBlockReason("a", 11.3f) == null,
            "turning to a different NPC does not erase a pending recently confirmed contact");
        check(contacts.ContactBlockReason("a", 14.79f) == null
            && contacts.ContactBlockReason("a", 14.81f) == "ContactObservationExpired",
            "a short loss of view bridges a contact report but never grants indefinite awareness");
        check(contacts.ContactBlockReason("unknown", 12) == "ContactForgotten", "no remembered sight means no contact report");
        contacts.Observe("a", false, false, 15);
        check(contacts.ContactBlockReason("a", 15) == "TargetAlreadyDown"
            && contacts.ContactBlockReason("b", 14) == null,
            "witnessing one target die cancels that target's contact, not another live target");
        check(contacts.DeathBlockReason("a", 15.1f) == "DeathAlreadyConfirmed",
            "subsequent corpse rays cannot report the same death again");
        contacts.Hostile("unseen", 16);
        check(contacts.DeathBlockReason("unseen", 16) == "NoLivingWitness"
            && contacts.Observe("unseen", false, false, 16) == null,
            "known hostility without a living sighting cannot turn a discovered corpse into a kill report");
        check(contacts.DeathBlockReason("neutral-corpse", 16) == "NoKnownHostility",
            "an unrelated corpse is not evidence of an enemy death");
        check(contacts.DeathBlockReason("b", 21.01f) == "LivingWitnessExpired",
            "an old living sighting explains why a later corpse is not reported");
        contacts.Clear();
        check(contacts.ContactBlockReason("b", 21) == "ContactForgotten", "menu/raid suspension discards pending enemy observations");
        var queue = new ReactionQueue();
        var contact = new Reaction { Kind = ReactionKind.Contact, Subject = "b", Priority = 80, ReadyAt = 10, ExpiresAt = 15,
            Cue = new VoiceCue { Key = "contact:b", Group = "contact", Intent = VoiceIntent.Contact } };
        queue.Offer(contact, 10); queue.RemoveSubject("contact", "a", "TargetAlreadyDown");
        check(queue.Next(11, _ => true, _ => true) == contact, "another target's death does not discard this queued contact");
        queue.RemoveSubject("contact", "b", "TargetAlreadyDown");
        check(queue.Count == 0, "this target's own death promptly removes its queued contact");

        var policy = new VoicePolicy();
        policy.Spoken(10, 2.57f, 8, true, false, 3,
            new VoiceCue { Key = "fight", Group = "fight", Intent = VoiceIntent.CombatBark });
        check(policy.BlockReason(contact.Cue, 12.8f) == "SpeechPlaying", "tactical information never interrupts a clip already playing");
        check(policy.BlockReason(contact.Cue, 13) == null,
            "contact can follow the finished clip without waiting out the ordinary monologue gap");
        check(policy.BlockReason(new VoiceCue { Intent = VoiceIntent.CalmMutter }, 13) == "GlobalGap",
            "giving tactical reports priority does not accelerate idle monologues");
        policy.Manual(13, 3);
        check(policy.BlockReason(contact.Cue, 14) == "ManualPriority", "tactical reports still yield to manual input");
        policy.Attempt(16);
        check(policy.BlockReason(contact.Cue, 17) == "PlaybackBackoff", "tactical reports cannot spin on native playback rejection");
        policy.Spoken(18, 1, 8, true, false, 3, new VoiceCue { Key = "contact:b", Group = "contact", GroupGap = 10, Repeat = 10 });
        check(policy.BlockReason(contact.Cue, 20) == "RecentSubject", "contact information still respects repeat suppression");
        check(VisualRules.TacticalReport(VoiceIntent.TacticalEnemyDown)
            && VisualRules.TacticalReport(VoiceIntent.EnemyDirection)
            && VisualRules.TacticalReport(VoiceIntent.TeamGetInCover)
            && !VisualRules.TacticalReport(VoiceIntent.KillTaunt) && !VisualRules.TacticalReport(VoiceIntent.KillConfirm),
            "team kill information can bypass ordinary spacing while solo emotional endings cannot");
        var freshReport = new BarkContext { Mode = VoiceMode.FikaClient, NearbyTeammate = true, RecentlyHit = true, DamageVersion = 7 };
        check(SemanticVoice.PendingRejectionReason(VoiceIntent.TeamSpreadOut, freshReport, 7) == null
            && SemanticVoice.WaitingReason(VoiceIntent.TeamSpreadOut, true) == "RecentDamage",
            "an emergency report waits out the immediate hit reaction instead of being discarded");
        freshReport.RecentlyHit = false; freshReport.DamageVersion = 8;
        check(SemanticVoice.RejectionReason(VoiceIntent.TeamSpreadOut, freshReport, 7) == "DamageAfterRequest",
            "new damage invalidates an older queued emergency report");

        check(VisualRules.RecognizableEnemy(false, true, true, false, true), "PMC can recognize an actually visible ordinary Scav before firing");
        check(VisualRules.RecognizableEnemy(false, true, true, true, false), "visible AI PMC is recognizable to local PMC");
        check(VisualRules.RecognizableEnemy(false, true, false, true, false), "player Scav can recognize an actually visible AI PMC");
        check(!VisualRules.RecognizableEnemy(false, true, false, false, true), "player Scav does not label ordinary Scavs hostile on sight");
        check(!VisualRules.RecognizableEnemy(true, true, true, true, true), "same-group affiliation overrides faction classification");
        check(!VisualRules.RecognizableEnemy(false, false, true, true, false), "a non-group human is not automatically presumed hostile");
        check(!VisualRules.RecognizableEnemy(false, true, true, false, false), "special or uncertain Scav roles still require direct hostility");

        var awareness = new SquadAwareness();
        awareness.Sight(0, true, false, 4, 0);
        check(!awareness.Nearby(0, true, 4, 0), "registering an unknown teammate behind the camera cannot invent familiarity");
        awareness.Sight(1, true, true, 4, 0);
        check(awareness.Nearby(1.2f, true, 4, 0), "seeing a nearby teammate establishes companion awareness");
        for (float t = 2; t <= 40; t++) awareness.Sight(t, true, false, 4, 0);
        check(awareness.Nearby(40, true, 4, 0), "continued unobstructed nearby company persists behind the camera");
        check(!awareness.Nearby(40, false, 4, 0), "a wall or expired LOS sample prevents hidden position sampling");
        check(!awareness.Nearby(40, true, 12.1f, 0) && !awareness.Nearby(40, true, 4, 2),
            "companion awareness is limited by distance and floor separation");
        awareness.Sight(45, false, true, 4, 0);
        check(!awareness.Nearby(48.1f, true, 4, 0), "a long obstruction expires familiarity even when facing the peer");
        awareness.Heard(50);
        check(awareness.Nearby(50, true, 4, 0), "an actually received nearby voice can reestablish companion awareness");
        awareness.Clear();
        check(!awareness.Nearby(50, true, 4, 0), "pausing or replacing a peer clears companion awareness");

        var squad = new SquadMemory(); squad.Bind(0); awareness.Sight(0, true, true, 4, 0);
        for (float t = 0; t <= 4; t += 0.25f)
        {
            awareness.Sight(t, true, t == 0, 4, 0);
            squad.Sample(0, t, P(t * 2), P(t * 2 - 4), awareness.Nearby(t, true, 4, 0));
        }
        var lead = squad.Decide(4, true, true, true);
        check(lead.Action == SquadAction.Leading && squad.Valid(lead, 4, true),
            "the real perception-plus-motion chain can invite a familiar teammate following behind");
        squad.Obscured(0, 4.25f, P(8.5f));
        check(!squad.Decide(4.25f, true, true, true).Exists, "occlusion still cannot originate a new companion statement");
        squad = new SquadMemory(); squad.Bind(0);
        for (float t = 0; t <= 4; t += 0.25f) squad.Sample(0, t, P(t * 2), P(t * 2 + 1, 4), true);
        check(squad.Decide(4, true, true, true).Action == SquadAction.Following,
            "loosely staggered companions can follow without walking a narrow straight file");
        squad = new SquadMemory(); squad.Bind(0);
        for (float t = 0; t <= 4; t += 0.25f) squad.Sample(0, t, P(t * 2), P(t * 2 + 1, 6), true);
        check(!squad.Decide(4, true, true, true).Exists, "excessive lateral separation still does not count as following");
        var context = new BarkContext { Mode = VoiceMode.FikaClient, NearbyTeammate = true, Combat = CombatPhase.Engaged };
        check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.TacticalEnemyDown,
            "an established nearby companion supports a factual death report even while the fight continues");
        squad = new SquadMemory(); squad.Bind(0); squad.Combat(true); squad.Threat("focus", P(40), 0);
        for (float t = 0; t <= 4; t += 0.25f)
        {
            squad.Threat("peripheral", P(40, 15), t, focused: false);
            squad.Threat("focus", P(40), t, focused: false);
            squad.Sample(0, t, P(t * 2), P(t * 2, 4), true);
        }
        var advance = squad.Decide(4, true, true, true);
        check(advance.Action == SquadAction.Advancing, "round-robin peripheral enemies cannot keep erasing joint advance evidence");
        squad.Threat("peripheral", P(40, 15), 4.1f);
        check(!squad.Valid(advance, 4.1f, true), "actually focusing a different threat still invalidates the old advance anchor");
        contacts = new ContactMemory(); contacts.Hostile("focus", 0); contacts.Observe("focus", true, false, 0);
        contacts.Observe("focus", true, false, 0.25f); contacts.Hostile("peripheral", 0.5f);
        contacts.Observe("peripheral", true, false, 4.3f, focused: false);
        check(contacts.Lost(4.3f, true) == "focus", "peripheral tracking does not replace the target of lost-visual memory");
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 51; i < 100051; i++)
        { awareness.Sight(i, true, true, 4, 0); awareness.Nearby(i, true, 4, 0); }
        check(GC.GetAllocatedBytesForCurrentThread() == allocated, "one hundred thousand companion awareness checks allocate no managed memory");
    }
}
