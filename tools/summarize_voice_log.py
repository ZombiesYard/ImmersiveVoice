"""Summarize recorded bark decisions, not audible sound or unlogged gameplay.

Usage: python tools/summarize_voice_log.py JOURNAL_DIRECTORY --timeline --output summary.json
Accepts multiple files/directories, optional --session ID, and legacy LogOutput.log.
Only [AutoVoice] records are read; player identities and unrelated logs are omitted.
"""
import collections
import json
import re
import argparse
import pathlib


FIELD = re.compile(r"(?:^| )([A-Za-z]\w*)=(.*?)(?= [A-Za-z]\w*=|$)")


def number(value):
    try:
        return float(value.replace(",", "."))
    except (ValueError, AttributeError):
        return None


REQUEST_FIELDS = ("Time", "Seq", "Event", "Decision", "Reason", "Intent", "Phrase", "Source", "ClipId", "Duration",
                  "SquadAction", "Peer", "PeerVersion", "Episode", "Reply", "DialogueCaller", "DialogueEpoch", "DialogueSequence", "ReadyVersion", "Combat", "Health", "Priority", "Group", "Remaining",
                  "ChancePercent", "RollPercent", "Passed", "HealthEpisode", "HealthState", "Confirmed", "NativeReached",
                  "Tags", "FemaleGateYielded", "BreathYielded", "CurrentPhrase", "QueuedPhrase",
                  "AcuteHealth", "HealthEscalation", "Teammate", "RecentTeammate", "RecentHit", "DamageVersion", "NpcEpisode", "NativeException")
SETTING_FIELDS = ("Version", "Mode", "Enabled", "Director", "Coordination", "Replies", "FriendlyDown", "Recording",
                  "Debug", "Gap", "ManualPause", "RespectStealth", "Combat", "Idle", "Visual", "Healing", "Damage", "MedicalRequests",
                  "CalmReloadChance", "RecoveryReloadChance", "VisualRange", "CombatMin", "CombatMax", "CombatChance",
                  "IdleMin", "IdleMax", "ContextInStealth", "InjuryCooldown", "NpcReplies", "NpcReplyCooldown")
PERFORMANCE_FIELDS = ("Samples", "SightChecks", "Decisions", "DiscoveryEntries", "Hits", "HitCallbacks", "Shots", "MeasuredTicks", "AverageMs", "MaxMs")


def diagnostics(sessions, fields, timeline):
    session_id = fields.get("Session")
    if not session_id:
        return
    session = sessions.setdefault(session_id, {
        "version": None, "mode": None, "voice": None, "started": False, "ended": False,
        "journal_closed": False, "capped": False, "dropped_records": 0,
        "decisions": collections.Counter(), "suppression_reasons": collections.Counter(),
        "waiting_reasons": collections.Counter(), "pause_reasons": collections.Counter(),
        "squad_silence_reasons": collections.Counter(), "squad_perceptions": collections.Counter(),
        "squad_ready_evidence": collections.Counter(), "squad_dialogue_decisions": collections.Counter(), "squad_dialogue_network": {},
        "visual_evidence": collections.defaultdict(collections.Counter), "settings": {}, "squad_performance": {}, "requests": {},
        "playback_results": collections.Counter(), "playback_compatibility": collections.Counter(),
        "treatment_actions": collections.Counter(), "squad_hit_rejections": collections.Counter(),
        "npc_heard_phrases": collections.Counter(), "npc_hearing_decisions": collections.Counter(),
        "npc_relations": collections.Counter(), "npc_dialogue_decisions": collections.Counter(), "npc_performance": {},
        "fault_counts": collections.Counter(), "fault_details": [],
    })
    event, decision, reason = fields.get("Event"), fields.get("Decision"), fields.get("Reason")
    for key in ("Version", "Mode", "Voice"):
        if fields.get(key):
            session[key.lower()] = fields[key]
    if event == "SessionStart":
        session["started"] = True
    elif event == "SessionEnd":
        session["ended"] = True
    elif event == "SessionPause":
        session["pause_reasons"][reason or "Unknown"] += 1
    elif event == "Settings":
        session["settings"] = {key: fields[key] for key in SETTING_FIELDS if key in fields}
    elif event == "SquadSummary":
        session["squad_performance"] = {key: number(fields.get(key)) for key in PERFORMANCE_FIELDS}
    elif event in ("JournalHealth", "JournalClosed"):
        session["dropped_records"] = max(session["dropped_records"], int(number(fields.get("Dropped")) or 0))
        session["journal_closed"] |= event == "JournalClosed"
        session["capped"] |= fields.get("Capped") == "True"
    if decision and (decision != "Played" or fields.get("Source")):
        session["decisions"][decision] += 1
    if decision == "Suppressed":
        session["suppression_reasons"][reason or "Unknown"] += 1
    elif decision == "Waiting":
        session["waiting_reasons"][reason or "Unknown"] += 1
    elif event == "SquadEvaluation" and decision == "Silent":
        session["squad_silence_reasons"][reason or "Unknown"] += 1
    if event in ("VisualTrack", "VisualDeath", "VisualThreat"):
        session["visual_evidence"][event][reason or "Unknown"] += 1
    elif event == "SquadPerception":
        session["squad_perceptions"][fields.get("State", "Unknown")] += 1
    elif event == "SquadReadyEvidence":
        session["squad_ready_evidence"][reason or "Unknown"] += 1
    elif event == "SquadDialogue":
        session["squad_dialogue_decisions"][reason or "Unknown"] += 1
    elif event == "SquadDialogueSummary":
        session["squad_dialogue_network"] = {key: number(fields.get(key)) for key in ("Sent", "Received", "Grants")}
        session["squad_dialogue_network"]["Ready"] = fields.get("Ready") == "True"
    elif event == "PlaybackResult":
        session["playback_results"][reason or "Unknown"] += 1
        for key in ("FemaleGateYielded", "BreathYielded"):
            if fields.get(key) == "True":
                session["playback_compatibility"][key] += 1
    elif event == "TreatmentAction":
        session["treatment_actions"][fields.get("State", "Unknown")] += 1
    elif event == "SquadHitRejected":
        session["squad_hit_rejections"][reason or "Unknown"] += 1
    elif event == "NpcHeard":
        session["npc_heard_phrases"][fields.get("HeardPhrase", "Unknown")] += 1
        session["npc_hearing_decisions"][reason or "Unknown"] += 1
        session["npc_relations"][fields.get("Relation", "Unknown")] += 1
    elif event == "NpcDecision":
        session["npc_dialogue_decisions"][reason or "Unknown"] += 1
    elif event == "NpcSummary":
        session["npc_performance"] = {key: number(fields.get(key)) for key in (
            "Ticks", "Captures", "NpcCaptures", "Callbacks", "RejectedSpeaker", "RejectedAudio", "OutOfRange",
            "HumanIgnored", "BreathIgnored", "Samples", "MeanMs", "MaxMs", "Capacity")}
    if event in ("Fault", "NpcFault", "PlaybackFault", "PlaybackRecovery"):
        session["fault_counts"][event + ":" + (reason or "Unknown")] += 1
        if len(session["fault_details"]) < 32:
            session["fault_details"].append({key: fields[key] for key in (
                "Event", "Time", "Request", "Phrase", "Component", "Stage", "Reason", "Stack", "Inner", "Count", "State") if key in fields})
    request_id = fields.get("Request")
    if request_id and request_id.isdigit() and request_id != "0":
        request = session["requests"].setdefault(request_id, {"intent": None, "phrase": None, "last_decision": None, "last_reason": None})
        request["intent"] = fields.get("Intent", request["intent"])
        request["phrase"] = fields.get("Phrase", request["phrase"])
        request["last_decision"] = decision or request["last_decision"]
        if decision:
            request["last_reason"] = reason
        if event == "PlaybackResult":
            request["last_playback_result"] = reason
            request["last_playback_confirmed"] = fields.get("Confirmed") == "True"
        if timeline:
            history = request.setdefault("history", [])
            if len(history) < 64:
                history.append({key: fields[key] for key in REQUEST_FIELDS if key in fields})
            else:
                request["history_truncated"] = True


def summarize(lines, timeline=False):
    voices = {}
    pending = {}
    windows = {}
    mismatches = []
    sessions = {}
    for line in lines:
        if "[AutoVoice]" not in line:
            continue
        fields = dict(FIELD.findall(line.split("[AutoVoice]", 1)[1].strip()))
        diagnostics(sessions, fields, timeline)
        voice = fields.get("Voice")
        if not voice:
            continue
        stats = voices.setdefault(voice, {
            "attempts": 0, "automatic_plays": 0, "native_or_manual_plays": 0,
            "missing_audio_callbacks": 0, "missing_phrase_decisions": collections.Counter(),
            "played_phrases": collections.Counter(), "clip_ids": collections.defaultdict(set),
            "max_automatic_starts_in_10_seconds": 0,
        })
        decision, phrase = fields.get("Decision"), fields.get("Phrase")
        bucket = (fields.get("Session", "legacy"), voice)
        when = number(fields.get("Time"))
        if decision == "Attempt":
            stats["attempts"] += 1
            pending[bucket] = (phrase, when)
        elif decision == "Suppressed" and fields.get("Reason") == "NoMatchingBank":
            stats["missing_phrase_decisions"][phrase] += 1
        elif decision == "Rejected" and fields.get("Reason") == "MissingAudioClip":
            stats["missing_audio_callbacks"] += 1
            pending.pop(bucket, None)
        elif decision == "Played" and fields.get("Source") == "Auto":
            # Director's Played telemetry has no Source and duplicates this
            # speaker record. It must not count as a second spoken line.
            if fields.get("HasAudio") == "False":
                stats["missing_audio_callbacks"] += 1
                pending.pop(bucket, None)
                continue
            stats["automatic_plays"] += 1
            stats["played_phrases"][phrase] += 1
            if fields.get("ClipId"):
                stats["clip_ids"][phrase].add(fields["ClipId"])
            request = pending.pop(bucket, None)
            if request and request[1] is not None and when is not None and abs(when - request[1]) <= 0.25 and request[0] != phrase:
                mismatches.append({"voice": voice, "time": when, "requested": request[0], "played": phrase})
            if when is not None:
                window = windows.setdefault(bucket, collections.deque())
                if window and when < window[-1]:
                    window.clear()  # A restarted client has a fresh Unity clock.
                while window and when - window[0] >= 10:
                    window.popleft()
                window.append(when)
                stats["max_automatic_starts_in_10_seconds"] = max(stats["max_automatic_starts_in_10_seconds"], len(window))
        elif decision == "Played" and fields.get("Source") == "Native/manual":
            stats["native_or_manual_plays"] += 1
    for stats in voices.values():
        stats["clip_ids"] = {phrase: sorted(ids) for phrase, ids in stats["clip_ids"].items()}
    return {
        "voices": voices,
        "sessions": sessions,
        "phrase_mismatches": mismatches,
        "note": "Only recorded decisions and speaker callbacks; not a listening or network test. Missing-phrase decisions are not raw event counts. Old logs without Time cannot measure density. Requests and clock windows are isolated by session; different clients' Unity clocks are not synchronized. Dropped/capped or unclosed journals are incomplete. Last settings are snapshots, not a full configuration history.",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", type=pathlib.Path, help="Log files or ImmersiveVoiceLogs directories")
    parser.add_argument("--timeline", action="store_true", help="Include per-request decision histories")
    parser.add_argument("--session", help="Read only this session ID")
    parser.add_argument("--output", type=pathlib.Path, help="Write the JSON report to this file")
    args = parser.parse_args()

    def read_lines():
        seen_paths = set()
        for path in args.paths:
            for source in sorted(path.glob("voice-*.log")) if path.is_dir() else [path]:
                canonical = source.resolve()
                if canonical in seen_paths:
                    continue
                seen_paths.add(canonical)
                with source.open(encoding="utf-8-sig", errors="replace") as log:
                    for line in log:
                        if args.session and dict(FIELD.findall(line.split("[AutoVoice]", 1)[-1].strip())).get("Session") != args.session:
                            continue
                        yield line

    report = json.dumps(summarize(read_lines(), args.timeline), ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(report + "\n", encoding="utf-8")
    else:
        print(report)
