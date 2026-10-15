using System;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.UI.Gestures;
using UnityEngine;
using System.IO;

namespace ImmersiveVoice
{
    internal sealed class VoiceSession : IDisposable
    {
        public readonly GamePlayerOwner Owner;
        public readonly GesturesQuickPanel Panel;
        public readonly Player Player;
        private readonly Settings _settings;
        private readonly FikaBridge _fika;
        private readonly ManualLogSource _log;
        private readonly BaseSpeaker _speaker;
        private readonly VoiceDirector _director;
        private readonly VoiceJournal _journal;
        private readonly VoicePolicy _policy = new VoicePolicy();
        private readonly System.Random _random = new System.Random();
        private float _idleAt, _combatAt;
        private bool _wasCombat, _wasHoldingCalm, _automatic, _automaticPlayed, _silent, _disposed, _wasSuspended;
        private VoiceCue _inflight;
        private int _attempts, _played;
        private readonly PlaybackRecovery _playbackRecovery = new PlaybackRecovery();
        private bool _playbackFaulted;
        private string _catalogVoice;
        private string _missingContext;
        private string _nativeHealthBlocked;
        private EPhraseTrigger _nativeHealthPhrase;
        private int _nativeHealthEpisode;
        private string _pauseReason, _lastSettings;
        private float _settingsAt;
        private bool _speakerBlocked;
        private EPhraseTrigger _lastPhrase;
        private TaggedClip _lastClip;
        private float _lastSpokenAt;
        private readonly Func<VoiceCue, string> _nativeGate;
        private bool Tracing => _settings.Debug.Value || _journal.Enabled;
        private void Trace(string fields)
        {
            _journal.Record(Time.time, fields);
            if (_settings.Debug.Value) _log.LogInfo(FormattableString.Invariant($"[AutoVoice] Session={_journal.Session} Time={Time.time:F3} {fields}"));
        }

        public VoiceSession(GamePlayerOwner owner, GesturesQuickPanel panel, Settings settings,
            FikaBridge fika, ManualLogSource log)
        {
            Owner = owner;
            Panel = panel;
            Player = owner.Player;
            _settings = settings;
            _fika = fika;
            _log = log;
            _speaker = Player.Speaker ?? throw new InvalidOperationException("Local speaker is not initialized.");
            _nativeGate = NativeGate;
            _journal = new VoiceJournal(Path.Combine(BepInEx.Paths.BepInExRootPath, "ImmersiveVoiceLogs"), message => log.LogWarning(message));
            _journal.Enable(settings.Recording.Value);
            try { _director = new VoiceDirector(Player, settings, fika, log, HasBank, _journal); }
            catch { _journal.Dispose(); throw; }
            _idleAt = Time.time + IdleInterval();
            _speaker.OnPhraseTold += OnSpoken;
            SpeechObservation.Played += _director.ObserveSpeech;
            _log.LogInfo("Attached automatic voice to local raid player. Current voice: " + _speaker.PlayerVoice);
            if (Tracing) Trace($"Event=SessionStart Version={Plugin.Version} Mode={_fika.Mode(Player)} Voice={_speaker.PlayerVoice} FemaleCompatibility={NativePlayback.FemaleCompatibility} TrackedTargets={TargetTracker.Capacity} PeerCapacity={SquadMemory.Capacity} SampleHz=4 SightHz=6 DecisionHz=2");
        }

        public void ToggleSilence()
        {
            _silent = !_silent;
            if (Tracing) Trace($"Event=SilenceToggle Enabled={_silent}");
            if (_silent) _director.Suspend();
            _log.LogInfo(_silent ? "Automatic voice: silent for this raid (Pause to resume)." : "Automatic voice: resumed.");
            EFT.Communications.NotificationManager.DisplayMessageNotification(_silent ? "自动角色语音：静默" : "自动角色语音：已恢复");
        }

        public void ManualInput()
        {
            if (!_automatic)
            {
                if (Tracing) Trace("Event=ManualInput Reason=ManualPriority");
                _policy.Manual(Time.time, _settings.ManualPause.Value);
                _director.DiscardPending();
            }
        }

        public void ObservedHit(Player victim, DamageInfo info) => _director.ObservedHit(victim, info);
        public void Fault(Exception ex) { if (Tracing) Trace("Event=Fault Component=Session " + FaultDetails.Format(ex)); }

        // False means the player/world was disposed, and all listeners must go.
        public bool Tick()
        {
            _journal.Enable(_settings.Recording.Value);
            if (_disposed || Owner == null || Panel == null || Player == null
                || Owner.State == PlayerOwner.EState.Stopped || _fika.Headless) return false;
            if (Owner.State != PlayerOwner.EState.Started)
            {
                Pause("RaidNotStarted");
                return true;
            }
            var world = Singleton<GameWorld>.Instance;
            if (world == null || world.MainPlayer == null)
            {
                Pause("WorldNotReady");
                return true;
            }
            if (world != Player.GameWorld || world.MainPlayer != Player) return false;
            float now = Time.time;
            RecordSettings(now);
            string pause = PauseReason(world);
            if (pause != null)
            {
                _policy.Observe(null, now, 0f);
                Pause(pause);
                return true;
            }
            if (_wasSuspended)
            {
                // No accumulated chatter fires immediately upon leaving menus,
                // regaining focus, being revived, or turning automation back on.
                _idleAt = now + IdleInterval();
                _combatAt = now + Range(2.5f, 5f);
                _wasSuspended = false;
                if (Tracing) Trace("Event=SessionResume Previous=" + _pauseReason);
                _pauseReason = null;
            }

            _director.Tick(now);
            bool holdCalm = _director.HoldingCalmSpeech;
            // A long treatment must not accumulate an overdue idle monologue.
            if (_wasHoldingCalm && !holdCalm) _idleAt = now + IdleInterval();
            _wasHoldingCalm = holdCalm;
            if (Tracing && _catalogVoice != _speaker.PlayerVoice) LogVoiceCatalog();
            bool combat = _director.ActiveCombat(now);
            if (combat != _wasCombat)
            {
                if (combat) _combatAt = now + Range(2.5f, 5f);
                else _idleAt = now + IdleInterval();
                _wasCombat = combat;
            }
            bool stealth = Player.IsInPronePose || Player.MovementContext.PoseLevel < 0.8f
                || Player.MovementContext.ClampedSpeed < 0.35f;
            bool aiming = Player.HandsController?.IsAiming == true;
            VoiceCue cue = null;
            if (_settings.AutoContext.Value && Panel._quickCommandObject != null
                && Panel._quickCommandObject.activeInHierarchy)
            {
                EPhraseTrigger phrase = Panel.PrioritizedCommand;
                if (phrase != EPhraseTrigger.None && phrase != EPhraseTrigger.PhraseNone
                    && !DirectedNative(phrase)
                    && (_settings.Cooperation.Value || phrase != EPhraseTrigger.Cooperation))
                {
                    string healthReason = _director.NativeHealthReason(phrase, now);
                    if (healthReason != null) TraceNativeHealth(phrase, healthReason);
                    else
                    {
                        _nativeHealthBlocked = null;
                        if (HasBank(phrase)) cue = MakeCue(phrase);
                        else
                        {
                            _director.ConsiderNativeHealth(phrase, missing: true);
                            string missing = _speaker.PlayerVoice + ":" + phrase;
                            if (Tracing && _missingContext != missing)
                                Trace($"Event=NativeContext Voice={_speaker.PlayerVoice} Phrase={phrase} Decision=Suppressed Reason=NoMatchingBank");
                            _missingContext = missing;
                        }
                    }
                }
            }
            if (cue != null || Panel._quickCommandObject == null || !Panel._quickCommandObject.activeInHierarchy) _missingContext = null;
            if (cue != null && !cue.Urgent && (aiming || (stealth && !_settings.ContextInStealth.Value))) cue = null;
            _policy.Observe(cue, now, cue?.Urgent == true ? Range(0.15f, 0.35f)
                : Range(_settings.ReactionMin.Value, _settings.ReactionMax.Value));

            // Maintain candidates even while speech is busy. A long, known
            // breathing loop can yield to an event; other native speech cannot.
            bool speakerBlocked = _speaker.Busy || (_speaker.QueuedEvent != EPhraseTrigger.PhraseNone
                && _speaker.QueuedEvent != EPhraseTrigger.None);
            if (speakerBlocked != _speakerBlocked)
            {
                _speakerBlocked = speakerBlocked;
                if (Tracing) Trace(FormattableString.Invariant($"Event=SchedulerGate Reason={(speakerBlocked ? "NativeSpeakerBusy" : "SpeakerReady")} CurrentPhrase={_lastPhrase} QueuedPhrase={_speaker.QueuedEvent} Remaining={Math.Max(0f, _lastSpokenAt + (_lastClip?.Length ?? 0f) - now):F2}"));
            }

            Reaction reaction = _director.Next(now, _policy, aiming, stealth, Player.IsSprintEnabled, _nativeGate);
            if (reaction != null)
            {
                if (!_director.Choose(reaction)) _director.Unavailable(reaction);
                else
                {
                    EPhraseTrigger phrase = (EPhraseTrigger)reaction.Cue.Phrase;
                    bool played = Attempt(reaction.Cue, () => SayNative(phrase));
                    _director.Completed(reaction, played, _playbackFaulted);
                    if (played && DirectedNative(phrase) && GesturesQuickPanel.PhrasePriorities.ContainsKey(phrase))
                        Panel.SetPhraseActive(phrase, false);
                    return true;
                }
            }
            if (cue != null && NativeGate(cue) == null && _policy.CanUseContext(now) && (cue.Urgent || !_director.OthersSpeaking(now)))
            {
                EPhraseTrigger phrase = (EPhraseTrigger)cue.Phrase;
                string healthReason = _director.NativeHealthReason(phrase, now);
                if (healthReason != null)
                {
                    TraceNativeHealth(phrase, healthReason);
                    _policy.Observe(null, now, 0f);
                    return true;
                }
                Attempt(cue, () => Owner.QuickMumbleStart());
                return true;
            }
            if (speakerBlocked || _director.Reserve(now) || _policy.ContextPending(now) || Player.IsSprintEnabled || (_settings.RespectStealth.Value && stealth)
                || !_policy.CanSpeak(now) || _director.OthersSpeaking(now)) return true;

            if (combat && _settings.Combat.Value && now >= _combatAt)
            {
                _combatAt = now + Range(_settings.CombatMin.Value, _settings.CombatMax.Value);
                VoiceCue bark = MakeCue(EPhraseTrigger.OnFight);
                string blocked = !SemanticVoice.StillValid(bark.Intent, _director.Context, 0) ? "SurvivalContext"
                    : _policy.BlockReason(bark, now);
                bool roll = _random.NextDouble() * 100 < _settings.CombatChance.Value;
                if (blocked == null && roll && HasBank(EPhraseTrigger.OnFight))
                    Attempt(bark, () => SayNative(EPhraseTrigger.OnFight));
                else if (Tracing)
                    Trace($"Event=CombatBark Voice={_speaker.PlayerVoice} Phrase=OnFight Decision=Suppressed Reason={blocked ?? (!roll ? "Probability" : "NoMatchingBank")}");
            }
            else if (!combat && !aiming && !holdCalm && !_director.Settling(now) && _settings.Idle.Value && now >= _idleAt)
            {
                _idleAt = now + IdleInterval();
                if (HasBank(EPhraseTrigger.OnMutter))
                    Attempt(MakeCue(EPhraseTrigger.OnMutter), () => SayNative(EPhraseTrigger.OnMutter));
                else if (Tracing) Trace($"Event=CalmMutter Voice={_speaker.PlayerVoice} Phrase=OnMutter Decision=Suppressed Reason=NoMatchingBank");
            }
            return true;
        }

        private bool CanYieldBreath(VoiceCue cue) => PlaybackRules.YieldBreath(ReferenceEquals(_lastClip, _speaker.Clip),
            _lastPhrase == EPhraseTrigger.OnBreath, _lastClip?.Length ?? 0f, Time.time - _lastSpokenAt, cue);

        private string NativeGate(VoiceCue cue)
        {
            bool yield = CanYieldBreath(cue);
            if (_speaker.QueuedEvent != EPhraseTrigger.PhraseNone && _speaker.QueuedEvent != EPhraseTrigger.None
                && !(yield && _speaker.QueuedEvent == EPhraseTrigger.OnBreath)) return "NativeQueuedPhrase";
            if (_speaker.Busy && !yield) return _lastPhrase == EPhraseTrigger.OnBreath ? "NativeBreath" : "NativeSpeakerBusy";
            return null;
        }

        private void TraceNativeHealth(EPhraseTrigger phrase, string reason)
        {
            if (!Tracing) return;
            int episode = _director.HealthEpisode;
            if (_nativeHealthBlocked == reason && _nativeHealthPhrase == phrase && _nativeHealthEpisode == episode) return;
            _nativeHealthBlocked = reason; _nativeHealthPhrase = phrase; _nativeHealthEpisode = episode;
            Trace($"Event=NativeHealth Voice={_speaker.PlayerVoice} Phrase={phrase} HealthEpisode={_director.HealthEpisode} HealthState={_director.HealthState} Decision=Suppressed Reason={reason}");
        }

        private string PauseReason(GameWorld world)
        {
            if (_fika.Downed(Player)) return "LocalDowned";
            if (Player.HealthController?.IsAlive != true) return "LocalDead";
            if (!RuntimeRules.IsLocalRaid(_fika.Headless, world is HideoutGameWorld, Player.IsYourPlayer, _fika.IsAI(Player),
                world.MainPlayer == Player, Player.gameObject.activeInHierarchy, true, false, Owner.State == PlayerOwner.EState.Started, false)) return "OwnershipOrLifecycle";
            if (!_settings.Enabled.Value) return "Disabled";
            if (_silent) return "SilenceToggle";
            if (_settings.FocusOnly.Value && !Application.isFocused) return "Unfocused";
            if (Player.IsInventoryOpened) return "Inventory";
            if (!Panel.gameObject.activeInHierarchy || Panel.GesturesMenu.IsShowing || Panel.DropdownPanelActive || Cursor.visible) return "Menu";
            if (Time.timeScale <= 0f) return "GamePaused";
            if (Player.VoipController?.Status?.Value == EVoipControllerStatus.Talking || (Player.VoipController?.TalkDetected?.Value ?? false)) return "Voip";
            return null;
        }
        private void Pause(string reason)
        {
            if (!_wasSuspended) _director.Suspend();
            if (_pauseReason != reason && Tracing) Trace("Event=SessionPause Reason=" + reason);
            _pauseReason = reason; _wasSuspended = true;
        }
        private void RecordSettings(float now)
        {
            if (!Tracing || now < _settingsAt) return;
            _settingsAt = now + 10f;
            string fields = FormattableString.Invariant($"Event=Settings Version={Plugin.Version} Mode={_fika.Mode(Player)} Enabled={_settings.Enabled.Value} Director={_settings.Director.Value} Coordination={_settings.SquadCoordination.Value} Replies={_settings.TeamReplies.Value} FriendlyDown={_settings.FriendlyDown.Value} Recording={_settings.Recording.Value} Debug={_settings.Debug.Value} Gap={_settings.Gap.Value:F2} ManualPause={_settings.ManualPause.Value:F2} RespectStealth={_settings.RespectStealth.Value} Combat={_settings.Combat.Value} CombatMin={_settings.CombatMin.Value:F2} CombatMax={_settings.CombatMax.Value:F2} CombatChance={_settings.CombatChance.Value:F2} Idle={_settings.Idle.Value} IdleMin={_settings.IdleMin.Value:F2} IdleMax={_settings.IdleMax.Value:F2} ContextInStealth={_settings.ContextInStealth.Value} InjuryCooldown={_settings.InjuryRepeat.Value:F2} Visual={_settings.Visual.Value} VisualRange={_settings.SightRange.Value:F1} Healing={_settings.Healing.Value} Damage={_settings.DamageReactions.Value} MedicalRequests={_settings.MedicalRequests.Value} CalmReloadChance={_settings.CalmReloadChance.Value:F2} RecoveryReloadChance={_settings.RecoveryReloadChance.Value:F2} NpcReplies={_settings.NpcReplies.Value} NpcReplyCooldown={_settings.NpcReplyCooldown.Value:F2}");
            if (fields == _lastSettings) return;
            _lastSettings = fields; Trace(fields);
        }

        private bool HasBank(EPhraseTrigger phrase)
        {
            if (!_speaker.PhrasesBanks.TryGetValue(phrase, out var bank) || bank == null || bank.Clips == null) return false;
            ETagStatus tags = Player.HealthStatus | (phrase == EPhraseTrigger.OnFight || Player.Awareness > Time.time
                ? ETagStatus.Combat : ETagStatus.Unaware);
            tags |= Player.GameWorld.SpeakerManager.GetGroupStatus(_speaker);
            foreach (TaggedClip clip in bank.Clips)
                if (clip?.Clip != null && clip.Length > 0f && (bank.IgnoreTags || TagBank.Compare(clip.Mask, (int)tags))) return true;
            return false;
        }

        private void SayNative(EPhraseTrigger phrase)
        {
            // The mod selects the meaning; EFT still selects the original F1
            // bank's clip with the same aggressive/Combat tags.
            if (phrase == EPhraseTrigger.OnFight) Player.Say(EPhraseTrigger.OnFight, demand: true, aggressive: true);
            else Player.Say(phrase, demand: true);
        }

        private bool DirectedNative(EPhraseTrigger phrase)
        {
            // Kill/hit HUD prompts may reveal events behind walls. Only the
            // director's visible evidence may authorize those automatic lines.
            if (phrase == EPhraseTrigger.OnEnemyDown || phrase == EPhraseTrigger.OnEnemyShot) return true;
            // A quick-panel phrase alone cannot establish medical shortage.
            if (phrase == EPhraseTrigger.NeedMedkit) return true;
            // Squad statements require the behaviour evidence held by the director.
            if (phrase == EPhraseTrigger.Going || phrase == EPhraseTrigger.Covering || phrase == EPhraseTrigger.Roger
                || phrase == EPhraseTrigger.GoForward || phrase == EPhraseTrigger.GetBack
                || phrase == EPhraseTrigger.FollowMe || phrase == EPhraseTrigger.CoverMe || phrase == EPhraseTrigger.Regroup
                || phrase == EPhraseTrigger.HoldPosition || phrase == EPhraseTrigger.OnFriendlyDown) return true;
            if (!_director.Directing) return false;
            return phrase == EPhraseTrigger.OnWeaponReload || phrase == EPhraseTrigger.NeedAmmo
                || phrase == EPhraseTrigger.OnWeaponJammed || phrase == EPhraseTrigger.WeaponBroken
                || phrase == EPhraseTrigger.OnWeaponMisfired || phrase == EPhraseTrigger.OnGrenade
                || phrase == EPhraseTrigger.OnEnemyGrenade;
        }

        private bool Attempt(VoiceCue cue, Action speak)
        {
            _director.SelectHealth(cue);
            _policy.Attempt(Time.time);
            _inflight = cue;
            _automatic = true;
            _automaticPlayed = false;
            _playbackFaulted = false;
            _attempts++;
            if (cue != null && cue.RequestId == 0) cue.RequestId = _journal.Request();
            NativePlayback.Begin(_speaker, (EPhraseTrigger)cue.Phrase, cue, CanYieldBreath(cue) ? _speaker.Clip : null);
            try
            {
                if (Tracing) Trace(FormattableString.Invariant($"Decision=Attempt Request={cue?.RequestId} Voice={_speaker.PlayerVoice} Intent={cue?.Intent} Phrase={(EPhraseTrigger)(cue?.Phrase ?? 0)} Mode={_director.Context.Mode} HealthEpisode={_director.HealthEpisode} HealthEscalation={_policy.HealthEscalation(cue, Time.time)}"));
                Exception error = NativePlayback.TrySpeak(speak);
                if (error != null)
                {
                    _playbackFaulted = true;
                    _playbackRecovery.Complete(true, _automaticPlayed);
                    if (Tracing) Trace($"Event=PlaybackFault Request={cue.RequestId} Phrase={(EPhraseTrigger)cue.Phrase} Count={_playbackRecovery.Failures} " + FaultDetails.Format(error));
                    _log.LogWarning("Automatic phrase failed; native state was left untouched. " + FaultDetails.Format(error));
                    // No polling-rate retries of a broken audio path. Isolated
                    // failures can recover; repeated failures silence automation.
                    if (_playbackRecovery.Silenced)
                    {
                        _silent = true;
                        _director.Suspend();
                        if (Tracing) Trace("Event=PlaybackRecovery State=Silenced Reason=RepeatedNativeExceptions");
                    }
                }
                else _playbackRecovery.Complete(false, _automaticPlayed);
            }
            finally
            {
                NativePlayback.End();
                _automatic = false; _inflight = null;
                if (!_automaticPlayed) _director.FailedHealth(cue);
                if (Tracing) Trace($"Event=PlaybackResult Request={cue.RequestId} Phrase={(EPhraseTrigger)cue.Phrase} Confirmed={_automaticPlayed} Reason={(_automaticPlayed ? "SpeakerConfirmed" : _playbackFaulted ? "NativePlaybackException" : NativePlayback.Result)} NativeException={_playbackFaulted} NativeReached={NativePlayback.Reached} Tags={(int)NativePlayback.Tags} FemaleGateYielded={NativePlayback.FemaleGateYielded} BreathYielded={NativePlayback.BreathYielded} CurrentPhrase={_lastPhrase} QueuedPhrase={_speaker.QueuedEvent} HealthState={_director.HealthState}");
            }
            return _automaticPlayed;
        }

        private void OnSpoken(EPhraseTrigger phrase, TaggedClip clip, TagBank bank, BaseSpeaker speaker)
        {
            // Event handlers must never throw through Fika's native speech path.
            try
            {
                bool physiological = phrase == EPhraseTrigger.OnBreath || phrase == EPhraseTrigger.OnBeingHurt
                    || phrase == EPhraseTrigger.OnAgony || phrase == EPhraseTrigger.OnDeath;
                bool hasAudio = clip?.Clip != null && clip.Length > 0f;
                bool own = _automatic && ReferenceEquals(speaker, _speaker) && _inflight?.Phrase == (int)phrase;
                if (!hasAudio)
                {
                    if (Tracing) Trace($"Decision=Rejected Request={(own ? _inflight.RequestId : 0)} Voice={speaker.PlayerVoice} Phrase={phrase} Reason=MissingAudioClip");
                    return;
                }
                if (own) { _automaticPlayed = true; _played++; }
                if (hasAudio) { _lastPhrase = phrase; _lastClip = clip; _lastSpokenAt = Time.time; }
                if (hasAudio && !physiological) _director.ConsiderNativeHealth(phrase);
                VoiceCue cue = own ? _inflight : MakeCue(phrase);
                _policy.Spoken(Time.time, clip?.Length ?? 0f, _settings.Gap.Value, own,
                    physiological, _settings.ManualPause.Value, cue, breathing: phrase == EPhraseTrigger.OnBreath);
                _director.Spoke(cue, own);
                if (!physiological)
                {
                    float end = Time.time + (clip?.Length ?? 0f);
                    _idleAt = end + IdleInterval();
                    _combatAt = end + Range(_settings.CombatMin.Value, _settings.CombatMax.Value);
                }
                if (Tracing) Trace(FormattableString.Invariant($"Decision=Played Request={cue?.RequestId} Source={(own ? "Auto" : "Native/manual")} Voice={speaker.PlayerVoice} Intent={cue?.Intent} Phrase={phrase} ClipId={clip?.NetId} Duration={clip?.Length:F2} HasAudio={hasAudio}"));
            }
            catch (Exception ex)
            {
                _silent = true;
                if (Tracing) Trace("Event=Fault Component=VoiceObservation " + FaultDetails.Format(ex));
                _log.LogError("Voice observation failed; automatic voice silenced for this raid. " + FaultDetails.Format(ex));
            }
        }

        private VoiceCue MakeCue(EPhraseTrigger phrase)
        {
            string group = phrase.ToString();
            string context = "";
            float repeat = _settings.Repeat.Value;
            float groupGap = repeat;
            bool urgent = phrase == EPhraseTrigger.OnGrenade || phrase == EPhraseTrigger.OnEnemyGrenade;
            VoiceIntent intent = VoiceIntent.NativeContext;
            float semanticRepeat = 0f;
            switch (phrase)
            {
                case EPhraseTrigger.OnFight:
                    group = "combat-emotion"; intent = VoiceIntent.CombatBark;
                    repeat = groupGap = semanticRepeat = _settings.CombatMin.Value;
                    break;
                case EPhraseTrigger.OnMutter:
                    group = "calm"; intent = VoiceIntent.CalmMutter;
                    // Existing ambient interval is the only timer for calm speech.
                    repeat = groupGap = 0f;
                    break;
                case EPhraseTrigger.LootKey: case EPhraseTrigger.LootMoney: case EPhraseTrigger.LootWeapon:
                case EPhraseTrigger.LootGeneric: case EPhraseTrigger.LootBody: case EPhraseTrigger.LootContainer:
                case EPhraseTrigger.CheckHim: case EPhraseTrigger.LootNothing:
                    group = "loot";
                    context = Player.InteractableObject != null ? Player.InteractableObject.GetInstanceID().ToString() : "none";
                    repeat = _settings.ObjectRepeat.Value;
                    groupGap = Math.Min(_settings.Repeat.Value, 30f);
                    break;
                case EPhraseTrigger.OpenDoor: case EPhraseTrigger.LockedDoor:
                    group = "door";
                    context = Player.InteractableObject != null ? Player.InteractableObject.GetInstanceID().ToString() : "none";
                    repeat = _settings.ObjectRepeat.Value;
                    groupGap = Math.Min(_settings.Repeat.Value, 30f);
                    break;
                case EPhraseTrigger.Cooperation:
                    context = Player.InteractablePlayer?.ProfileId ?? "none";
                    repeat = Math.Max(repeat, 120f);
                    break;
                case EPhraseTrigger.HurtLight: case EPhraseTrigger.HurtMedium: case EPhraseTrigger.HurtHeavy:
                case EPhraseTrigger.HurtNearDeath: case EPhraseTrigger.Bleeding: case EPhraseTrigger.HandBroken:
                case EPhraseTrigger.LegBroken: case EPhraseTrigger.Dehydrated: case EPhraseTrigger.Exhausted:
                case EPhraseTrigger.UnderFire: case EPhraseTrigger.FriendlyFire:
                    group = "health";
                    context = phrase.ToString();
                    repeat = groupGap = _settings.InjuryRepeat.Value;
                    break;
                case EPhraseTrigger.ExitLocated:
                    repeat = Math.Max(repeat, 120f);
                    break;
                case EPhraseTrigger.OnWeaponReload:
                    group = "reload";
                    context = (Player.HandsController as Player.FirearmController)?.Item?.Id.ToString() ?? "none";
                    urgent = true;
                    repeat = groupGap = _settings.ReloadRepeat.Value;
                    break;
                case EPhraseTrigger.NeedAmmo: case EPhraseTrigger.OnOutOfAmmo:
                    group = "ammo"; urgent = true; repeat = groupGap = 8f;
                    break;
                case EPhraseTrigger.OnWeaponJammed: case EPhraseTrigger.WeaponBroken: case EPhraseTrigger.OnWeaponMisfired:
                    group = "malfunction"; urgent = true; repeat = groupGap = 8f;
                    break;
                case EPhraseTrigger.StartHeal:
                    group = "heal"; repeat = groupGap = 12f;
                    break;
                case EPhraseTrigger.Going: case EPhraseTrigger.Covering: case EPhraseTrigger.Roger:
                case EPhraseTrigger.GoForward: case EPhraseTrigger.GetBack:
                case EPhraseTrigger.FollowMe: case EPhraseTrigger.CoverMe: case EPhraseTrigger.Regroup: case EPhraseTrigger.HoldPosition:
                    group = phrase == EPhraseTrigger.Roger ? "team-response"
                        : phrase == EPhraseTrigger.GetBack ? "team-retreat"
                        : phrase == EPhraseTrigger.CoverMe ? "team-request" : "team-coordination";
                    repeat = groupGap = SquadMemory.DecisionGap;
                    intent = phrase == EPhraseTrigger.Going ? VoiceIntent.TeamFollowing
                        : phrase == EPhraseTrigger.Covering ? VoiceIntent.TeamCovering
                        : phrase == EPhraseTrigger.GoForward ? VoiceIntent.TeamAdvance
                        : phrase == EPhraseTrigger.GetBack ? VoiceIntent.TeamRetreat
                        : phrase == EPhraseTrigger.FollowMe ? VoiceIntent.TeamLead
                        : phrase == EPhraseTrigger.CoverMe ? VoiceIntent.TeamNeedCover
                        : phrase == EPhraseTrigger.Regroup ? VoiceIntent.TeamRegroup
                        : phrase == EPhraseTrigger.HoldPosition ? VoiceIntent.TeamHold : VoiceIntent.Acknowledge;
                    break;
                case EPhraseTrigger.OnFriendlyDown:
                    group = "friendly-down"; repeat = groupGap = 45f; intent = VoiceIntent.FriendlyDown;
                    break;
                case EPhraseTrigger.NeedMedkit:
                    group = "medical-request"; repeat = groupGap = MedicalRequestPolicy.Repeat;
                    intent = VoiceIntent.MedicalRequest;
                    break;
                case EPhraseTrigger.OnFirstContact: case EPhraseTrigger.OnRepeatedContact: case EPhraseTrigger.InTheFront:
                    group = "contact"; repeat = groupGap = 10f;
                    break;
                case EPhraseTrigger.EnemyDown: case EPhraseTrigger.OnEnemyDown:
                    group = "kill"; repeat = groupGap = 7f;
                    break;
                case EPhraseTrigger.LostVisual: case EPhraseTrigger.OnLostVisual:
                    group = "lost"; repeat = groupGap = 12f;
                    break;
                case EPhraseTrigger.OnGrenade: case EPhraseTrigger.OnEnemyGrenade:
                    group = phrase == EPhraseTrigger.OnEnemyGrenade ? "grenade-warning" : "grenade-out";
                    intent = phrase == EPhraseTrigger.OnEnemyGrenade ? VoiceIntent.GrenadeWarning : VoiceIntent.GrenadeOut;
                    repeat = groupGap = 2f;
                    break;
            }
            return new VoiceCue { Phrase = (int)phrase, Key = group + ":" + context, Group = group,
                Repeat = repeat, GroupGap = groupGap, Urgent = urgent, Intent = intent, SemanticRepeat = semanticRepeat,
                HealthSeverity = group == "health" ? _director.Context.Injury : InjuryLevel.Healthy,
                HeavyBleeding = group == "health" && _director.Context.HeavyBleeding };
        }

        private void LogVoiceCatalog()
        {
            _catalogVoice = _speaker.PlayerVoice;
            foreach (EPhraseTrigger phrase in Enum.GetValues(typeof(EPhraseTrigger)))
            {
                _speaker.PhrasesBanks.TryGetValue(phrase, out var bank);
                Trace($"Event=Catalog Voice={_catalogVoice} Phrase={phrase} Clips={bank?.Clips?.Length ?? 0} MatchingNow={HasBank(phrase)} Health={Player.HealthStatus}");
            }
        }

        private float IdleInterval() => Range(_settings.IdleMin.Value, _settings.IdleMax.Value);
        private float Range(float a, float b) => Math.Min(a, b) + (float)_random.NextDouble() * Math.Abs(b - a);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _speaker.OnPhraseTold -= OnSpoken;
                SpeechObservation.Played -= _director.ObserveSpeech;
                _director.Dispose();
            }
            finally
            {
                try
                {
                    if (Tracing) Trace($"Event=SessionEnd Attempts={_attempts} ConfirmedAutomaticClips={_played} UnconfirmedAttempts={_attempts - _played}");
                }
                finally { _journal.Dispose(); }
            }
        }
    }
}
