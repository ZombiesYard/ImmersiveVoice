using ImmersiveVoice;

internal static class EncounterScenarios
{
    public static void Run(Action<bool, string> check)
    {
        BarkContext Context(CombatMemory fight, float now) => new()
        {
            Combat = fight.Active(now) ? CombatPhase.Engaged : fight.Settling(now) ? CombatPhase.Recovering : CombatPhase.Calm,
            FightDuration = fight.Duration(now), PeakPressure = fight.PeakPressure(now),
            TookHostileHit = fight.TookHostileHit(now), HadMalfunction = fight.HadMalfunction(now),
            RecentObservedDowns = fight.RecentObservedDowns(now), EndingSelected = fight.EndingSelected
        };

        var fight = new CombatMemory();
        fight.Shot(10, true, false); fight.Defeat(10.1f, false);
        var context = Context(fight, 11);
        check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.Silent
            && SemanticVoice.EnemyDown(context, false) == VoiceIntent.Silent && !SemanticVoice.Recovery(context),
            "one brief unhurt Solo encounter can end in silence regardless of the taunt roll");
        foreach (var mode in new[] { VoiceMode.FikaHost, VoiceMode.FikaClient })
        {
            context.Mode = mode; context.NearbyTeammate = true;
            check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.TacticalEnemyDown,
                mode + " can report an easy observed down for a nearby human teammate");
            context.Combat = CombatPhase.Engaged;
            check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.TacticalEnemyDown,
                mode + " keeps useful down reports during an ongoing exchange");
            context.NearbyTeammate = false;
            check(!SemanticVoice.StillValid(VoiceIntent.TacticalEnemyDown, context, 0),
                mode + " cancels a queued team report after losing the nearby audience");
        }

        // Feed only ContactMemory's actual observed transitions, never kill credit.
        var contacts = new ContactMemory();
        contacts.Hostile("a", 10); contacts.Hostile("b", 10);
        contacts.Observe("a", true, false, 10); contacts.Observe("b", true, false, 10);
        fight = new CombatMemory(); fight.Shot(10, true, false);
        foreach (var id in new[] { "a", "a", "b", "unseen" })
            if (contacts.Observe(id, false, false, 11) == ReactionKind.EnemyDown) fight.Defeat(11, false);
        context = Context(fight, 12);
        check(context.RecentObservedDowns == 2 && SemanticVoice.EnemyDown(context, true) == VoiceIntent.KillTaunt,
            "two distinct recently observed hostile deaths qualify without duplicate corpses or hidden kill credit");
        context.Combat = CombatPhase.Engaged;
        check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.Silent,
            "an unfinished Solo exchange suppresses celebration even after several observed downs");
        context = Context(fight, 12); context.Injury = InjuryLevel.Injured;
        check(SemanticVoice.EnemyDown(context, false) == VoiceIntent.Silent && SemanticVoice.Recovery(context),
            "a wounded Solo survivor favors the remaining health condition over an ending report");
        check(fight.RecentObservedDowns(23) == 0, "observed-down sequences expire after twelve seconds");
        for (int i = 0; i < 20; i++) fight.Defeat(30 + i * 0.1f, false);
        check(fight.RecentObservedDowns(32) == 8, "observed-down history stays bounded");
        fight.Clear();
        check(fight.RecentObservedDowns(32) == 0, "suspension clears observed-down context");

        fight.Shot(40, true, false); fight.Malfunction(41); fight.Defeat(42, false);
        context = Context(fight, 43);
        check(context.HadMalfunction && SemanticVoice.EnemyDown(context, false) == VoiceIntent.KillConfirm,
            "a genuine fault encountered during a short fight can qualify its ending");
        fight.SelectEnding(); context = Context(fight, 43);
        check(!SemanticVoice.Recovery(context),
            "a selected down ending prevents an unrelated recovery line even when its exact clip is missing");
        context.Bleeding = true;
        check(SemanticVoice.Recovery(context), "an ending choice does not suppress a real remaining wound");
        int oldExchange = fight.ExchangeVersion;
        fight.Shot(44, false, false);
        check(fight.ExchangeVersion == oldExchange && fight.EndingSelected,
            "test fire at no threat cannot reset the ending decision");
        fight.Shot(45, true, false); fight.Defeat(45.1f, false);
        check(fight.ExchangeVersion != oldExchange && !fight.EndingSelected && !fight.Active(46),
            "a resumed fight invalidates old endings even if it ends before the next scheduler tick");
        fight.Malfunction(60);
        check(!fight.HadMalfunction(60) && !fight.Active(60), "old faults cannot renew expired encounter evidence");
        fight.Clear(); fight.Malfunction(70);
        check(!fight.HadMalfunction(70) && !fight.Active(70) && !fight.Settling(70),
            "a fault outside combat creates neither pressure nor recovery speech");
        fight.Incoming(80); fight.Defeat(80.1f, false);
        context = Context(fight, 81); context.RecentlyHit = true;
        check(context.TookHostileHit && SemanticVoice.Recovery(context, waiting: true) && !SemanticVoice.Recovery(context),
            "a fresh impact may settle during the recovery delay but cannot authorize immediate speech");
        context.RecentlyHit = false;
        check(SemanticVoice.Recovery(context), "an actual incoming hit still matters after the brief hit guard expires");
        context.Healing = true;
        check(!SemanticVoice.Recovery(context, waiting: true), "treatment prevents even a deferred recovery candidate");
        check(!fight.TookHostileHit(96), "hostile-hit experience expires with the encounter");

        Reaction Ending(string subject, float ready, float expiry, int version = 1) => new()
        {
            Kind = ReactionKind.EnemyDown, Subject = subject, ReadyAt = ready, ExpiresAt = expiry,
            Priority = 40, ExchangeVersion = version, Cue = new VoiceCue { Group = "kill" }
        };
        var queue = new ReactionQueue();
        var first = Ending("a", 12, 17); queue.OfferEnding(first, 10);
        var second = Ending("b", 14, 20); queue.OfferEnding(second, 13);
        check(queue.Count == 1 && second.ExpiresAt == 17 && queue.Next(14, _ => true, _ => true) == second,
            "a distinct observed down replaces the pending ending without extending the first expiry");
        var late = Ending("c", 18, 22); queue.OfferEnding(late, 16);
        check(queue.Next(18, _ => true, _ => true) == null && queue.Count == 0,
            "continuous observed downs cannot postpone an ending indefinitely");
        queue.OfferEnding(Ending("old", 21, 25), 20);
        var resumed = Ending("new", 22, 28, 2); queue.OfferEnding(resumed, 21);
        check(resumed.ExpiresAt == 28 && queue.Next(22, _ => true, _ => true) == resumed,
            "a new exchange gets its own expiry instead of inheriting a stale ending");

        var faults = new MalfunctionMemory();
        check(faults.Begin(null, true) == 0 && faults.Begin("good", false) == 0,
            "a missing weapon or repaired weapon cannot authorize a fault line");
        faults.Observe("a", true);
        int episode = faults.Begin("a", true);
        check(episode != 0 && faults.Matches("a", episode),
            "state observation alone leaves the one decision for an actual native hint");
        bool repeated = false;
        for (int i = 0; i < 100; i++) repeated |= faults.Begin("a", true) != 0;
        check(!repeated, "repeated checks and changed native hint meanings cannot reopen an unresolved fault");
        faults.Observe("b", false); faults.Observe(null, false); queue.Clear(); faults.Observe("a", true);
        check(faults.Begin("a", true) == 0 && faults.Matches("a", episode),
            "switching guns, using medicine or discarding pending speech preserves the fault decision");
        // Begin is independent of playback: missing audio, expiry and manual
        // speech all leave the same unresolved episode decided.
        check(faults.Begin("a", true) == 0, "a missing exact fault clip does not grant another phrase a fallback attempt");
        faults.Observe("a", false);
        check(!faults.Matches("a", episode), "repair invalidates pending speech about the old fault");
        int next = faults.Begin("a", true);
        check(next != 0 && next != episode && !faults.Matches("a", episode),
            "a genuinely new fault gets one new decision without reviving the old candidate");
        check(faults.Begin("b", true) != 0 && !faults.Matches("b", next),
            "separate weapons have separate fault episodes");
        faults.Clear();
        check(faults.Count == 0 && faults.Begin("a", true) != 0, "a new raid has no previous fault decisions");
        for (int i = 0; i < 30; i++) faults.Begin("weapon" + i, true);
        check(faults.Count == 16 && faults.Begin("overflow", true) == 0 && faults.Begin("a", true) == 0,
            "fault memory is bounded and refuses overflow without evicting and repeating an unresolved fault");
        faults.Observe("a", false);
        check(faults.Begin("overflow", true) != 0 && faults.Count == 16,
            "observing a repair releases capacity for another weapon");
    }
}
