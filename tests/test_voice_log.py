import importlib.util
import pathlib
import json
import subprocess
import sys
import tempfile
import unittest

path = pathlib.Path(__file__).resolve().parents[1] / "tools/summarize_voice_log.py"
spec = importlib.util.spec_from_file_location("voice_log", path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class VoiceLogTests(unittest.TestCase):
    def test_npc_dialogue_relationships_and_bounded_work_are_reported(self):
        report = module.summarize([
            "[AutoVoice] Session=a Event=NpcHeard SourceVoice=Scav_1 Relation=Neutral HeardPhrase=FollowMe Decision=Observed Reason=AudibleNpcSpeech",
            "[AutoVoice] Session=a Event=NpcDecision HeardPhrase=FollowMe Decision=Waiting Reason=AwaitMatchingContextOrAction",
            "[AutoVoice] Session=a Event=NpcDecision HeardPhrase=FollowMe Decision=Candidate Reason=Following",
            "[AutoVoice] Session=a Event=NpcSummary Ticks=100 Captures=5 NpcCaptures=4 Callbacks=20 RejectedSpeaker=10 RejectedAudio=1 OutOfRange=1 HumanIgnored=2 BreathIgnored=1 Samples=40 MeanMs=0.01 MaxMs=0.05 Capacity=8",
        ])
        session = report["sessions"]["a"]
        self.assertEqual(session["npc_relations"], {"Neutral": 1})
        self.assertEqual(session["npc_dialogue_decisions"], {"AwaitMatchingContextOrAction": 1, "Following": 1})
        self.assertEqual(session["npc_performance"]["Samples"], 40)
        self.assertEqual(session["npc_performance"]["Capacity"], 8)
        self.assertEqual(session["npc_performance"]["MeanMs"], 0.01)
        self.assertEqual(session["npc_performance"]["Callbacks"], 20)
        self.assertEqual(session["npc_performance"]["NpcCaptures"], 4)
        self.assertEqual(session["npc_performance"]["HumanIgnored"], 2)

    def test_native_fault_recovery_and_session_faults_remain_visible(self):
        report = module.summarize([
            "[AutoVoice] Session=a Time=1 Request=4 Decision=Attempt Voice=Custom Phrase=OnWeaponReload",
            "[AutoVoice] Session=a Time=1 Request=4 Event=PlaybackFault Phrase=OnWeaponReload Count=1 Reason=NullReferenceException Stack=Speaker.Play@12",
            "[AutoVoice] Session=a Time=1 Request=4 Event=PlaybackResult Confirmed=False Reason=NativePlaybackException NativeException=True",
            "[AutoVoice] Session=a Time=1 Request=4 Decision=Suppressed Reason=NativePlaybackException",
            "[AutoVoice] Session=a Time=5 Request=5 Decision=Attempt Voice=Custom Phrase=OnFirstContact",
            "[AutoVoice] Session=a Time=5 Request=5 Decision=Played Voice=Custom Phrase=OnFirstContact Source=Auto HasAudio=True",
            "[AutoVoice] Session=a Time=10 Event=Fault Component=Session Reason=InvalidOperationException Stack=Director.Tick@6",
        ], timeline=True)
        session = report["sessions"]["a"]
        self.assertEqual(session["fault_counts"], {"PlaybackFault:NullReferenceException": 1, "Fault:InvalidOperationException": 1})
        self.assertEqual(session["fault_details"][0]["Stack"], "Speaker.Play@12")
        self.assertEqual(session["requests"]["4"]["last_decision"], "Suppressed")
        self.assertFalse(session["requests"]["4"]["last_playback_confirmed"])
        self.assertEqual(report["voices"]["Custom"]["automatic_plays"], 1)
        old = module.summarize(["[AutoVoice] Session=old Event=NpcSummary Captures=0"])["sessions"]["old"]
        self.assertIsNone(old["npc_performance"]["Callbacks"])
        flooded = module.summarize(["[AutoVoice] Session=a Event=NpcFault Reason=Exception"] * 100)["sessions"]["a"]
        self.assertEqual(len(flooded["fault_details"]), 32)
        self.assertEqual(flooded["fault_counts"]["NpcFault:Exception"], 100)

    def test_npc_observations_are_not_counted_as_local_voice_plays(self):
        report = module.summarize([
            "[AutoVoice] Session=a Event=Settings NpcReplies=True NpcReplyCooldown=30.00",
            "[AutoVoice] Session=a Event=NpcHeard SourceVoice=Scav_1 HeardPhrase=OnFight Decision=Candidate Reason=AudibleHostileSpeech NpcEpisode=3",
            "[AutoVoice] Session=a Event=NpcHeard SourceVoice=Scav_2 HeardPhrase=OnFight Decision=Suppressed Reason=AudioTooQuiet",
            "[AutoVoice] Session=a Request=5 Event=NpcReply Voice=Usec_5 Intent=NpcRetort Phrase=OnFight Decision=Candidate NpcEpisode=3",
            "[AutoVoice] Session=a Request=5 Decision=Played Source=Auto Voice=Usec_5 Phrase=OnFight HasAudio=True",
        ], timeline=True)
        session = report["sessions"]["a"]
        self.assertEqual(session["settings"]["NpcReplyCooldown"], "30.00")
        self.assertEqual(session["npc_hearing_decisions"], {"AudibleHostileSpeech": 1, "AudioTooQuiet": 1})
        self.assertEqual(session["requests"]["5"]["history"][0]["NpcEpisode"], "3")
        self.assertEqual(list(report["voices"]), ["Usec_5"])
        self.assertEqual(report["voices"]["Usec_5"]["automatic_plays"], 1)

    def test_hit_wait_is_not_a_drop_or_play_and_new_damage_remains_terminal(self):
        report = module.summarize([
            "[AutoVoice] Session=a Time=10 Request=1 Event=EnemyDown Decision=Candidate Voice=Usec_1 Intent=TacticalEnemyDown Phrase=EnemyDown RecentHit=True DamageVersion=6/6",
            "[AutoVoice] Session=a Time=11 Request=1 Event=EnemyDown Decision=Waiting Voice=Usec_1 Reason=RecentDamage RecentHit=True DamageVersion=6/6",
            "[AutoVoice] Session=a Time=12 Request=1 Event=EnemyDown Decision=Suppressed Voice=Usec_1 Reason=DamageAfterRequest RecentHit=True DamageVersion=6/7",
        ], timeline=True)
        session = report["sessions"]["a"]
        self.assertEqual(session["waiting_reasons"], {"RecentDamage": 1})
        self.assertEqual(session["suppression_reasons"], {"DamageAfterRequest": 1})
        request = session["requests"]["1"]
        self.assertEqual(request["history"][1]["RecentHit"], "True")
        self.assertEqual(request["history"][1]["DamageVersion"], "6/6")
        self.assertEqual(request["history"][2]["DamageVersion"], "6/7")
        self.assertEqual(request["last_reason"], "DamageAfterRequest")
        self.assertEqual((report["voices"]["Usec_1"]["attempts"], report["voices"]["Usec_1"]["automatic_plays"]), (0, 0))

    def test_raid_regression_diagnostics_keep_evidence_separate_from_playback(self):
        report = module.summarize([
            "[AutoVoice] Session=a Event=Settings InjuryCooldown=20.00",
            "[AutoVoice] Session=a Time=10 Request=1 Event=Injury Decision=Candidate Voice=Bear_1 Phrase=HurtNearDeath AcuteHealth=True",
            "[AutoVoice] Session=a Time=10.1 Request=1 Decision=Attempt Voice=Bear_1 Phrase=HurtNearDeath HealthEscalation=True",
            "[AutoVoice] Session=a Time=10.1 Request=1 Decision=Played Source=Auto Voice=Bear_1 Phrase=HurtNearDeath HasAudio=True",
            "[AutoVoice] Session=a Time=20 Request=2 Event=EnemyDown Decision=Candidate Voice=Bear_1 Phrase=EnemyDown Teammate=False RecentTeammate=True",
            "[AutoVoice] Session=a Time=20 Request=3 Event=Squad Decision=Suppressed Voice=Bear_1 Phrase=Covering Reason=SquadCoverTargetDown",
        ], timeline=True)
        session = report["sessions"]["a"]
        self.assertEqual(session["settings"]["InjuryCooldown"], "20.00")
        self.assertEqual(session["requests"]["1"]["history"][0]["AcuteHealth"], "True")
        self.assertEqual(session["requests"]["1"]["history"][1]["HealthEscalation"], "True")
        self.assertEqual(session["requests"]["2"]["history"][0]["Teammate"], "False")
        self.assertEqual(session["requests"]["2"]["history"][0]["RecentTeammate"], "True")
        self.assertEqual(session["suppression_reasons"]["SquadCoverTargetDown"], 1)
        self.assertEqual(report["voices"]["Bear_1"]["automatic_plays"], 1)

    def test_attempt_results_do_not_duplicate_speaker_confirmation(self):
        report = module.summarize([
            "[AutoVoice] Session=a Time=1 Request=4 Decision=Attempt Voice=Custom Phrase=StartHeal",
            "[AutoVoice] Session=a Time=1 Request=4 Event=PlaybackResult Phrase=StartHeal Confirmed=False Reason=NativeNoBankReturned NativeReached=False FemaleGateYielded=False BreathYielded=False HealthState=Ready",
            "[AutoVoice] Session=a Time=3 Request=4 Decision=Attempt Voice=Custom Phrase=StartHeal",
            "[AutoVoice] Session=a Time=3 Request=4 Decision=Played Voice=Custom Phrase=StartHeal Source=Auto HasAudio=True",
            "[AutoVoice] Session=a Time=3 Request=4 Event=PlaybackResult Phrase=StartHeal Confirmed=True Reason=SpeakerConfirmed NativeReached=True FemaleGateYielded=True BreathYielded=True Tags=65",
        ], timeline=True)
        session = report["sessions"]["a"]
        self.assertEqual(session["playback_results"], {"NativeNoBankReturned": 1, "SpeakerConfirmed": 1})
        self.assertEqual(session["playback_compatibility"], {"FemaleGateYielded": 1, "BreathYielded": 1})
        request = session["requests"]["4"]
        self.assertEqual(request["last_decision"], "Played")
        self.assertTrue(request["last_playback_confirmed"])
        self.assertEqual(request["history"][-1]["Tags"], "65")
        self.assertEqual((report["voices"]["Custom"]["attempts"], report["voices"]["Custom"]["automatic_plays"]), (2, 1))

    def test_medical_and_hit_diagnostics_are_evidence_counts(self):
        report = self.report(
            "Session=a Event=TreatmentAction State=ControllerObserved Injury=BadlyInjured",
            "Session=a Event=TreatmentAction State=ControllerEnded",
            "Session=a Event=SquadHitRejected Reason=PeerNotPerceived TotalCallbacks=3",
            "Session=a Event=Settings CombatMin=18.00 CombatMax=32.00 CombatChance=65.00 IdleMin=90.00 IdleMax=180.00 ContextInStealth=False",
            "Session=a Event=SquadSummary HitCallbacks=8 Hits=2")
        session = report["sessions"]["a"]
        self.assertEqual(session["treatment_actions"], {"ControllerObserved": 1, "ControllerEnded": 1})
        self.assertEqual(session["squad_hit_rejections"], {"PeerNotPerceived": 1})
        self.assertEqual(session["settings"]["CombatChance"], "65.00")
        self.assertEqual(session["squad_performance"]["HitCallbacks"], 8)
        self.assertEqual(session["requests"], {})
        self.assertEqual(report["voices"], {})

    def test_cli_directory_session_filter_and_output(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = pathlib.Path(temp)
            source = folder / "voice-a.log"
            source.write_text("[AutoVoice] Session=a Time=2 Decision=Played Request=1 Voice=Usec_1 Phrase=FollowMe Source=Auto HasAudio=True\n", encoding="utf-8-sig")
            (folder / "voice-b.log").write_text("[AutoVoice] Session=b Time=2 Decision=Played Request=1 Voice=Usec_1 Phrase=Roger Source=Auto HasAudio=True\n", encoding="utf-8")
            (folder / "unrelated.log").write_text("[AutoVoice] Session=a Decision=Played Voice=Usec_1 Source=Auto HasAudio=True\n", encoding="utf-8")
            output = folder / "report.json"
            subprocess.run([sys.executable, str(path), str(folder), str(source), "--session", "a", "--timeline", "--output", str(output)], check=True, capture_output=True, text=True)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(list(report["sessions"]), ["a"])
            self.assertEqual(report["voices"]["Usec_1"]["automatic_plays"], 1)
            self.assertEqual(report["sessions"]["a"]["requests"]["1"]["history"][0]["Phrase"], "FollowMe")

    def report(self, *lines):
        return module.summarize("[Info] [AutoVoice] " + line for line in lines)

    def test_missing_phrase_is_silent_not_success(self):
        report = self.report("Event=RepeatedContact Voice=Usec_2 Phrase=OnRepeatedContact Decision=Suppressed Reason=NoMatchingBank")
        voice = report["voices"]["Usec_2"]
        self.assertEqual(voice["automatic_plays"], 0)
        self.assertEqual(voice["attempts"], 0)
        self.assertEqual(voice["missing_phrase_decisions"], {"OnRepeatedContact": 1})

    def test_health_gates_are_not_missing_audio_or_attempts(self):
        report = module.summarize([
            "[AutoVoice] Session=a Time=1 Request=1 Event=Injury Voice=Scav_6 Phrase=HurtMedium HealthEpisode=2 Decision=Candidate Reason=Event",
            "[AutoVoice] Session=a Time=2 Request=1 Event=Injury Voice=Scav_6 Phrase=HurtMedium HealthEpisode=2 Decision=Suppressed Reason=NoMatchingBank",
            "[AutoVoice] Session=a Time=22 Event=NativeHealth Voice=Scav_6 Phrase=HurtLight HealthEpisode=2 Decision=Suppressed Reason=SameHealthEpisode",
            "[AutoVoice] Session=a Time=25 Event=NativeHealth Voice=Scav_6 Phrase=HurtLight HealthEpisode=2 Decision=Suppressed Reason=HealingInProgress",
        ], timeline=True)
        session = report["sessions"]["a"]
        self.assertEqual(session["suppression_reasons"], {
            "NoMatchingBank": 1, "SameHealthEpisode": 1, "HealingInProgress": 1})
        self.assertEqual(session["requests"]["1"]["history"][0]["HealthEpisode"], "2")
        voice = report["voices"]["Scav_6"]
        self.assertEqual((voice["attempts"], voice["automatic_plays"]), (0, 0))
        self.assertEqual(voice["missing_phrase_decisions"], {"HurtMedium": 1})

    def test_visual_evidence_and_companions_are_not_speech(self):
        report = self.report(
            "Session=a Event=Settings Version=2.9.0 VisualRange=60.0",
            "Session=a Event=VisualThreat Decision=Observed Reason=VisibleFaction",
            "Session=a Event=VisualTrack TargetSlot=0 TargetVersion=1 Reason=OutsideViewCone",
            "Session=a Event=VisualTrack TargetSlot=0 TargetVersion=1 Reason=VisibleCorpse",
            "Session=a Event=VisualDeath Decision=Observed Corpse=True Reason=RecentLivingWitness",
            "Session=a Event=SquadPerception Peer=0 State=NearbyKnown")
        session = report["sessions"]["a"]
        self.assertEqual(session["visual_evidence"]["VisualTrack"], {"OutsideViewCone": 1, "VisibleCorpse": 1})
        self.assertEqual(session["visual_evidence"]["VisualDeath"], {"RecentLivingWitness": 1})
        self.assertEqual(session["squad_perceptions"], {"NearbyKnown": 1})
        self.assertEqual(session["settings"]["VisualRange"], "60.0")
        self.assertEqual(session["requests"], {})
        self.assertEqual(report["voices"], {})

    def test_actual_clip_not_double_counted(self):
        report = self.report(
            "Decision=Attempt Voice=Bear_3 Time=10.00 Phrase=OnWeaponReload",
            "Decision=Played Source=Auto Voice=Bear_3 Time=10.00 Phrase=OnWeaponReload ClipId=4 Duration=1.2 HasAudio=True",
            "Event=Reload Voice=Bear_3 Phrase=OnWeaponReload Decision=Played Reason=SpeakerConfirmed",
            "Decision=Played Source=Native/manual Voice=Bear_3 Time=12.00 Phrase=OnFight")
        voice = report["voices"]["Bear_3"]
        self.assertEqual((voice["attempts"], voice["automatic_plays"], voice["native_or_manual_plays"]), (1, 1, 1))
        self.assertEqual(voice["clip_ids"], {"OnWeaponReload": ["4"]})
        self.assertEqual(report["phrase_mismatches"], [])

    def test_missing_audio_is_not_a_play(self):
        report = self.report("Decision=Attempt Voice=Custom Time=1 Phrase=StartHeal",
                             "Decision=Rejected Voice=Custom Phrase=StartHeal Reason=MissingAudioClip")
        voice = report["voices"]["Custom"]
        self.assertEqual((voice["automatic_plays"], voice["missing_audio_callbacks"]), (0, 1))

    def test_unexpected_substitution_is_visible(self):
        report = self.report("Decision=Attempt Voice=Custom Time=1.00 Phrase=StartHeal",
                             "Decision=Played Source=Auto Voice=Custom Time=1.00 Phrase=CoverMe HasAudio=True")
        self.assertEqual(report["phrase_mismatches"], [{"voice": "Custom", "time": 1.0, "requested": "StartHeal", "played": "CoverMe"}])

    def test_density_uses_half_open_windows_and_clock_resets(self):
        report = self.report(*(f"Decision=Played Source=Auto Voice=Custom Time={when} Phrase=OnFight HasAudio=True"
                               for when in (10, 14, 20, 1, 2)))
        self.assertEqual(report["voices"]["Custom"]["max_automatic_starts_in_10_seconds"], 2)

    def test_partial_old_logs_do_not_invent_density(self):
        report = self.report("Decision=Played Source=Auto Voice=Custom Voice Phrase=OnMutter")
        self.assertEqual(report["voices"]["Custom Voice"]["max_automatic_starts_in_10_seconds"], 0)
        self.assertEqual(report["phrase_mismatches"], [])
        self.assertEqual(module.summarize(["an unrelated log with a private player identity"])["voices"], {})

    def test_request_timeline_has_session_scope_and_reasons(self):
        report = module.summarize([
            "[AutoVoice] Session=a Event=SessionStart Version=2.8.0 Mode=FikaHost Voice=Bear",
            "[AutoVoice] Session=a Time=1 Request=1 Event=Squad Intent=TeamLead Phrase=FollowMe Decision=Candidate Reason=Event",
            "[AutoVoice] Session=a Time=2 Request=1 Event=Squad Decision=Waiting Reason=NearbySpeech",
            "[AutoVoice] Session=a Time=4 Request=1 Event=Squad Decision=Suppressed Reason=Expired",
            "[AutoVoice] Session=b Time=1 Request=1 Event=Squad Intent=TeamFollowing Phrase=Going Decision=Candidate Reason=Event",
        ], timeline=True)
        request = report["sessions"]["a"]["requests"]["1"]
        self.assertEqual(request["last_reason"], "Expired")
        self.assertEqual([r["Decision"] for r in request["history"]], ["Candidate", "Waiting", "Suppressed"])
        self.assertEqual(report["sessions"]["b"]["requests"]["1"]["phrase"], "Going")
        self.assertEqual(report["sessions"]["a"]["waiting_reasons"], {"NearbySpeech": 1})

    def test_clients_do_not_share_density_or_pending_requests(self):
        report = self.report(
            "Session=a Decision=Attempt Voice=Bear Time=1 Phrase=Going",
            "Session=b Decision=Played Source=Auto Voice=Bear Time=1 Phrase=Roger HasAudio=True",
            "Session=a Decision=Played Source=Auto Voice=Bear Time=1 Phrase=Going HasAudio=True")
        self.assertEqual(report["voices"]["Bear"]["automatic_plays"], 2)
        self.assertEqual(report["voices"]["Bear"]["max_automatic_starts_in_10_seconds"], 1)
        self.assertEqual(report["phrase_mismatches"], [])

    def test_reload_probability_is_distinct_from_missing_audio(self):
        report = module.summarize([
            "[AutoVoice] Session=a Event=Settings CalmReloadChance=50.00 RecoveryReloadChance=65.00",
            "[AutoVoice] Session=a Time=1 Request=1 Event=ReloadRoll Voice=Scav_6 Phrase=OnWeaponReload ChancePercent=50.000 RollPercent=70.000 Passed=False",
            "[AutoVoice] Session=a Time=1 Request=1 Event=Reload Voice=Scav_6 Phrase=OnWeaponReload Decision=Suppressed Reason=ReloadProbability",
        ], timeline=True)
        session = report["sessions"]["a"]
        self.assertEqual(session["settings"]["CalmReloadChance"], "50.00")
        self.assertEqual(session["settings"]["RecoveryReloadChance"], "65.00")
        request = session["requests"]["1"]
        self.assertEqual(request["history"][0]["ChancePercent"], "50.000")
        self.assertEqual(request["history"][0]["RollPercent"], "70.000")
        self.assertEqual(request["history"][0]["Passed"], "False")
        self.assertEqual(request["last_reason"], "ReloadProbability")
        self.assertEqual(report["voices"]["Scav_6"]["missing_phrase_decisions"], {})
        self.assertEqual(report["voices"]["Scav_6"]["attempts"], 0)

    def test_diagnostic_records_without_voice_are_preserved(self):
        report = self.report(
            "Session=a Event=SessionPause Reason=Voip",
            "Session=a Event=SquadEvaluation Decision=Silent Reason=NoFreshObservation",
            "Session=a Event=SquadSummary Samples=4 SightChecks=6 Decisions=2 AverageMs=0.0123 MaxMs=0.0456",
            "Session=a Event=JournalHealth Dropped=7",
            "Session=a Event=JournalClosed Records=10 Dropped=7 Capped=True")
        session = report["sessions"]["a"]
        self.assertEqual(session["pause_reasons"], {"Voip": 1})
        self.assertEqual(session["squad_silence_reasons"], {"NoFreshObservation": 1})
        self.assertEqual(session["squad_performance"]["AverageMs"], 0.0123)
        self.assertTrue(session["capped"] and session["journal_closed"])
        self.assertEqual(session["dropped_records"], 7)
        self.assertFalse(session["ended"])

    def test_unclosed_journal_is_not_invented_as_complete(self):
        report = self.report("Session=a Event=SessionStart Version=2.8.0 Mode=FikaClient")
        session = report["sessions"]["a"]
        self.assertTrue(session["started"])
        self.assertFalse(session["ended"] or session["journal_closed"])

    def test_report_does_not_copy_unrelated_private_fields(self):
        report = module.summarize([
            "[AutoVoice] Session=a Event=Settings Mode=FikaHost Nickname=private-name Account=private-account",
            "[AutoVoice] Session=a Event=Squad Request=1 Decision=Candidate Nickname=private-name Position=private-position",
        ], timeline=True)
        import json
        self.assertNotIn("private-", json.dumps(report))

    def test_timeline_is_optional_and_bounded(self):
        lines = ["[AutoVoice] Session=a Request=1 Decision=Waiting Reason=Busy"] * 100
        self.assertNotIn("history", module.summarize(lines)["sessions"]["a"]["requests"]["1"])
        request = module.summarize(lines, timeline=True)["sessions"]["a"]["requests"]["1"]
        self.assertEqual(len(request["history"]), 64)
        self.assertTrue(request["history_truncated"])


if __name__ == "__main__":
    unittest.main()
