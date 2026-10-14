using System;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using BepInEx.Logging;
using UnityEngine;

namespace ImmersiveVoice
{
    internal sealed class VoiceDirector : IDisposable
    {
        private readonly Player _player;
        private readonly Settings _settings;
        private readonly FikaBridge _fika;
        private readonly ManualLogSource _log;
        private readonly VoiceJournal _journal;
        private readonly HealthSpeechMemory _healthSpeech = new HealthSpeechMemory();
        private bool Tracing => _settings.Debug.Value || _journal.Enabled;
        private void Record(string fields)
        {
            _journal.Record(Time.time, fields);
            if (_settings.Debug.Value) _log.LogInfo(FormattableString.Invariant($"[AutoVoice] Session={_journal.Session} Time={Time.time:F3} {fields}"));
        }
        private readonly ReactionQueue _queue;
        private readonly DamageWindow _damage = new DamageWindow();
        private readonly ReloadDecision _reloadDecision = new ReloadDecision();
        private readonly MalfunctionMemory _malfunctions = new MalfunctionMemory();
        private readonly TreatmentMemory _treatment = new TreatmentMemory();
        private readonly SquadObserver _squad;
        private readonly NpcReplyObserver _npc;
        private static readonly EBodyPart[] TreatmentParts = { EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach,
            EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg };
        private readonly MedicalRequestDecision _medicalRequest = new MedicalRequestDecision();
        private readonly MedicalSupplyProbe _medicalSupplyProbe;
        private MedicalSupplies _medicalSupplies = MedicalSupplies.Unknown;
        private float _lastMedicalScan = -1000f;
        private static readonly int KindCount = Enum.GetValues(typeof(ReactionKind)).Length;
        private readonly int[] _generated = new int[KindCount], _played = new int[KindCount], _suppressed = new int[KindCount];
        private int _damageVersion, _rawHits, _recoveryVersion = -1;
        private float _lastDamage = -1000f;
        private readonly ContactMemory _memory = new ContactMemory();
        private readonly CombatMemory _combat = new CombatMemory();
        private readonly System.Random _random = new System.Random();
        private readonly ViewProbe _probe = new ViewProbe();
        private readonly TargetTracker _targets;
        private readonly Func<EPhraseTrigger, bool> _hasClip;
        private Player.FirearmController _gun;
        private object _hands;
        private VisibleContact _gaze, _ally;
        private float _lastVision = -100f, _lastAllySeen = -100f, _lastShot = -100f;
        private bool _collecting, _disposed, _faulted, _reload, _empty;
        private string _deathTraceIdentity, _deathTraceReason;
        private float _deathTraceAt;

        public VoiceDirector(Player player, Settings settings, FikaBridge fika, ManualLogSource log,
            Func<EPhraseTrigger, bool> hasClip, VoiceJournal journal)
        {
            _player = player; _settings = settings; _fika = fika; _log = log; _hasClip = hasClip; _journal = journal;
            _targets = new TargetTracker(_probe, Record);
            _medicalSupplyProbe = new MedicalSupplyProbe(player, log);
            _queue = new ReactionQueue(Dropped);
            _squad = new SquadObserver(player, fika, settings, log, journal);
            _npc = new NpcReplyObserver(player, fika, _memory.IsHostile, Record);
            player.PhraseSituation += OnNativeSituation;
            player.BeingHitAction += OnHit;
        }

        private bool Collect => !_disposed && !_faulted && _collecting && _settings.Enabled.Value
            && _player != null && _player.HealthController?.IsAlive == true && !_player.IsInventoryOpened;
        private string WeaponId => _gun?.Item?.Id.ToString();
        private bool WeaponFaulty => _gun != null && _gun.Item.MalfState.State != Weapon.EMalfunctionState.None;
        public bool Directing => !_faulted && _settings.Director.Value;

        public bool ActiveCombat(float now) => Collect && _combat.Active(now);
        public bool Settling(float now) => _combat.Settling(now);
        public bool HoldingCalmSpeech => _player.HandsController is Player.MedsController || _treatment.Tracking;
        public int HealthEpisode => _healthSpeech.Version;
        public HealthDisposition HealthState => _healthSpeech.Disposition;
        internal static HealthCue HealthMeaning(EPhraseTrigger phrase)
        {
            switch (phrase)
            {
                case EPhraseTrigger.HurtLight: case EPhraseTrigger.HurtMedium: return HealthCue.Injury;
                case EPhraseTrigger.HurtHeavy: return HealthCue.SevereInjury;
                case EPhraseTrigger.HurtNearDeath: return HealthCue.CriticalInjury;
                case EPhraseTrigger.Bleeding: return HealthCue.Bleeding;
                case EPhraseTrigger.HandBroken: return HealthCue.ArmFracture;
                case EPhraseTrigger.LegBroken: return HealthCue.LegFracture;
                default: return HealthCue.None;
            }
        }
        public string NativeHealthReason(EPhraseTrigger phrase, float now)
        {
            HealthCue meaning = HealthMeaning(phrase);
            if (meaning == HealthCue.None) return null;
            BarkContext context = Context;
            _healthSpeech.Observe(now, context);
            return _healthSpeech.BlockReason(meaning, context, now, _queue.ContainsGroup("health"),
                _treatment.Tracking || _queue.ContainsGroup("medical-request"))
                ?? (_healthSpeech.MatchesSelection((int)phrase) ? null : "HealthPhraseAlreadySelected");
        }
        public void ConsiderNativeHealth(EPhraseTrigger phrase, bool missing = false)
        {
            if (HealthMeaning(phrase) == HealthCue.None) return;
            _healthSpeech.Observe(Time.time, Context);
            if (missing) _healthSpeech.Missing(); else _healthSpeech.Consider();
        }
        public void FailedHealth(VoiceCue cue)
        {
            if (WoundCue(cue)) _healthSpeech.Failed();
        }
        public void SelectHealth(VoiceCue cue)
        {
            if (WoundCue(cue)) _healthSpeech.Select(cue.Phrase);
        }
        private static bool WoundCue(VoiceCue cue) => cue?.Group == "health"
            && (cue.Intent != VoiceIntent.NativeContext || HealthMeaning((EPhraseTrigger)cue.Phrase) != HealthCue.None);
        public bool OthersSpeaking(float now) => _squad.OthersSpeaking(now);
        private bool CoverWeaponReady => _gun != null && !_reload && !WeaponFaulty;
        public void ObservedHit(Player victim, DamageInfo info) { if (Collect) _squad.Hit(victim, info); }
        public void ObserveSpeech(Player speaker, TaggedClip clip, BetterSource source)
        {
            if (Collect && Directing && _settings.NpcReplies.Value) _npc.Capture(speaker, clip, source);
        }

        public BarkContext Context => new BarkContext
        {
            Mode = _fika.Mode(_player),
            Combat = _combat.Active(Time.time) ? CombatPhase.Engaged
                : (_combat.Settling(Time.time) ? CombatPhase.Recovering : CombatPhase.Calm),
            Injury = HealthLevel(), Healing = _player != null && _player.HandsController is Player.MedsController,
            RecentlyHit = Time.time - _lastDamage < SemanticVoice.RecentHitWindow, DamageVersion = _damageVersion,
            FightDuration = _combat.Duration(Time.time), PeakPressure = _combat.PeakPressure(Time.time),
            TookHostileHit = _combat.TookHostileHit(Time.time), HadMalfunction = _combat.HadMalfunction(Time.time),
            RecentObservedDowns = _combat.RecentObservedDowns(Time.time), EndingSelected = _combat.EndingSelected,
            Bleeding = _player?.HealthController?.FindActiveEffect<IBleeding>() != null,
            HeavyBleeding = _player?.HealthController?.FindActiveEffect<IHeavyBleeding>() != null,
            ArmFracture = HasFracture(EBodyPart.LeftArm) || HasFracture(EBodyPart.RightArm),
            LegFracture = HasFracture(EBodyPart.LeftLeg) || HasFracture(EBodyPart.RightLeg),
            NearbyTeammate = _squad.Nearby(Time.time, 12f) || (_ally?.Player != null && !_ally.Player.IsAI
                && _ally.Alive && Time.time - _lastAllySeen <= 1f),
            RecentTeammate = _squad.ReportNearby(Time.time)
        };

        private bool HasFracture(EBodyPart part) => _player?.HealthController?.FindActiveEffect<IFracture>(part) != null;

        private InjuryLevel HealthLevel()
        {
            if (_player == null || _player.HealthController == null) return InjuryLevel.Dying;
            switch (_player.HealthStatus)
            {
                case ETagStatus.Dying: return InjuryLevel.Dying;
                case ETagStatus.BadlyInjured: return InjuryLevel.BadlyInjured;
                case ETagStatus.Injured: return InjuryLevel.Injured;
                default: return InjuryLevel.Healthy;
            }
        }

        public void Tick(float now)
        {
            if (_faulted) return;
            if (!_settings.Director.Value) _queue.Clear();
            _collecting = true;
            _npc.Active(_settings.Director.Value && _settings.NpcReplies.Value);
            if (_npc.Tick(now, Context, Tracing))
            {
                _queue.RemoveKind(ReactionKind.NpcReply, "NewNpcExchange");
                Offer(ReactionKind.NpcReply, "npc-reply", SemanticVoice.TauntPriority,
                    _npc.ReadyAt - now, _npc.ExpiresAt - now, _settings.NpcReplyCooldown.Value,
                    null, EPhraseTrigger.OnFight, VoiceIntent.NpcRetort, npcEpisode: _npc.Version);
            }
            _healthSpeech.Observe(now, Context);
            FlushDamage(now);
            var hands = _player.HandsController;
            if (!ReferenceEquals(hands, _hands))
            {
                bool wasMedicine = _hands is Player.MedsController oldMeds && oldMeds.Item is Meds;
                bool isMedicine = hands is Player.MedsController newMeds && newMeds.Item is Meds;
                if (Tracing && (wasMedicine || isMedicine))
                    Record($"Event=TreatmentAction State={(isMedicine ? "ControllerObserved" : "ControllerEnded")} HealthEpisode={HealthEpisode} Injury={Context.Injury} Bleeding={Context.Bleeding} HeavyBleeding={Context.HeavyBleeding}");
                if (_gun != null) _gun.OnShot -= OnShot;
                _hands = hands;
                _gun = hands as Player.FirearmController;
                if (_gun != null) _gun.OnShot += OnShot;
                _queue.RemoveGroup("reload"); _queue.RemoveGroup("ammo"); _queue.RemoveGroup("malfunction");
                _reload = _empty = false;
                _reloadDecision.Clear();
                _queue.RemoveGroup("heal");
                if (hands is Player.MedsController meds && meds.Item is Meds)
                {
                    // Starting treatment invalidates old reactions even when its
                    // start bark is disabled or the original voice lacks StartHeal.
                    _damage.Clear();
                    _queue.RemoveGroup("health", "TreatmentStarted");
                    _queue.RemoveGroup("recovery", "TreatmentStarted");
                    _queue.RemoveGroup("kill", "TreatmentStarted");
                    _queue.RemoveGroup("medical-request", "TreatmentStarted");
                    _recoveryVersion = _combat.ExchangeVersion;
                    _combat.SelectEnding();
                    BarkContext context = Context;
                    if (_settings.Director.Value && _settings.Healing.Value && _settings.DamageReactions.Value)
                        _treatment.Begin(now, context, ReadTreatmentSnapshot(context), _combat.ExchangeVersion);
                    else _treatment.Cancel();
                    if (_settings.Healing.Value)
                        Offer(ReactionKind.Heal, "heal", 82, 0.3f, 7f, 12f, null, EPhraseTrigger.StartHeal);
                }
            }
            _malfunctions.Observe(WeaponId, WeaponFaulty);
            bool reloading = _gun != null && _gun.IsInReloadOperation();
            _reloadDecision.Update(now, reloading);
            if (reloading && !_reload && _settings.AutoContext.Value) QueueReload();
            if (!reloading && _reload) _queue.RemoveGroup("reload");
            _reload = reloading;
            if (_gun != null && _settings.AutoContext.Value)
            {
                bool empty = _gun.Item.ChamberAmmoCount + _gun.Item.GetCurrentMagazineCount() == 0;
                if (empty && !_empty && now - _lastShot < 1f)
                    Offer(ReactionKind.Ammo, "ammo", 96, 0.3f, 6f, 10f, WeaponId, EPhraseTrigger.OnOutOfAmmo);
                _empty = empty;
            }
            if (now - _lastVision >= 0.2f && (_settings.Combat.Value || _settings.Visual.Value || _settings.TeamReplies.Value
                || _settings.Healing.Value || _settings.MedicalRequests.Value || _squad.Enabled))
            {
                _lastVision = now;
                UpdateVision(now);
            }
            bool combat = _combat.Active(now);
            if (!combat && _combat.Settling(now) && _recoveryVersion != _combat.ExchangeVersion)
            {
                _recoveryVersion = _combat.ExchangeVersion;
                // Also handles a whole short exchange between ticks. Fresh damage
                // may settle during the delay, but any further damage cancels it.
                BarkContext context = Context;
                if (SemanticVoice.Recovery(context, waiting: true))
                {
                    bool wounded = context.Wounded;
                    Offer(ReactionKind.Recovery, wounded ? "health" : "recovery", 30, 3f, 12f,
                        wounded ? _settings.InjuryRepeat.Value : 40f, null,
                        wounded ? InjuryPhrase() : EPhraseTrigger.OnMutter,
                        wounded ? SemanticVoice.Injury(context, false) : VoiceIntent.Recovery);
                }
            }
            if (_settings.Visual.Value)
            {
                string lost = _memory.Lost(now, combat);
                if (lost != null) Offer(ReactionKind.LostVisual, "lost", 55, 0.3f, 4f, 12f, lost, EPhraseTrigger.OnLostVisual);
            }
            UpdateMedicalRequest(now);
            UpdateTreatment(now);
            BarkContext squadContext = Context;
            SquadOwnAction ownAction = _hands is Player.MedsController treatment && treatment.Item is Meds ? SquadOwnAction.Healing
                : WeaponFaulty ? SquadOwnAction.WeaponFault : _reload ? SquadOwnAction.Reloading : SquadOwnAction.None;
            SquadDecision decision = _squad.Enabled ? _squad.Tick(now, squadContext, CoverWeaponReady, ownAction) : default;
            if (!_squad.Enabled) _squad.Pause(now);
            if (decision.Exists)
            {
                VoiceIntent intent = decision.Reply ? VoiceIntent.Acknowledge
                    : decision.Action == SquadAction.Following ? VoiceIntent.TeamFollowing
                    : decision.Action == SquadAction.Covering ? VoiceIntent.TeamCovering
                    : decision.Action == SquadAction.Advancing ? VoiceIntent.TeamAdvance
                    : decision.Action == SquadAction.Leading ? VoiceIntent.TeamLead
                    : decision.Action == SquadAction.RequestCover ? VoiceIntent.TeamNeedCover
                    : decision.Action == SquadAction.Rallying ? VoiceIntent.TeamRegroup
                    : decision.Action == SquadAction.HoldTeam ? VoiceIntent.TeamHold : VoiceIntent.TeamRetreat;
                EPhraseTrigger phrase = decision.Reply ? EPhraseTrigger.Roger
                    : decision.Action == SquadAction.Following ? EPhraseTrigger.Going
                    : decision.Action == SquadAction.Covering ? EPhraseTrigger.Covering
                    : decision.Action == SquadAction.Advancing ? EPhraseTrigger.GoForward
                    : decision.Action == SquadAction.Leading ? EPhraseTrigger.FollowMe
                    : decision.Action == SquadAction.RequestCover ? EPhraseTrigger.CoverMe
                    : decision.Action == SquadAction.Rallying ? EPhraseTrigger.Regroup
                    : decision.Action == SquadAction.HoldTeam ? EPhraseTrigger.HoldPosition : EPhraseTrigger.GetBack;
                string group = decision.Reply ? "team-response"
                    : decision.Action == SquadAction.Retreating ? "team-retreat"
                    : decision.Action == SquadAction.RequestCover ? "team-request" : "team-coordination";
                if (decision.Reply || decision.Action == SquadAction.Retreating || decision.Action == SquadAction.RequestCover)
                    _queue.RemoveKind(ReactionKind.Squad, "NewSquadSituation");
                Offer(ReactionKind.Squad, group, 60, 0.2f + (float)_random.NextDouble() * 0.2f,
                    3f, SquadMemory.DecisionGap, _squad.Subject(decision), phrase, intent, squad: decision);
            }
            if (!squadContext.Survival && !OthersSpeaking(now))
            {
                SquadLoss loss = _squad.Loss(now);
                if (loss.Exists)
                    Offer(ReactionKind.FriendlyDown, "friendly-down", 74, 0.8f + (float)_random.NextDouble() * 0.7f,
                        5f, 45f, null, EPhraseTrigger.OnFriendlyDown, VoiceIntent.FriendlyDown, loss: loss);
            }
        }

        private TreatmentSnapshot ReadTreatmentSnapshot(BarkContext context)
        {
            var snapshot = new TreatmentSnapshot
            {
                Health = _player.HealthController.GetBodyPartHealth(EBodyPart.Common).Current,
                Injury = context.Injury
            };
            for (int i = 0; i < TreatmentParts.Length; i++)
            {
                EBodyPart part = TreatmentParts[i];
                if (_player.HealthController.FindActiveEffect<IBleeding>(part) != null) snapshot.Bleeds |= 1 << i;
                if (_player.HealthController.FindActiveEffect<IHeavyBleeding>(part) != null) snapshot.HeavyBleeds |= 1 << i;
                if (HasFracture(part)) snapshot.Fractures |= 1 << i;
            }
            return snapshot;
        }

        private void UpdateTreatment(float now)
        {
            if (!_treatment.Tracking) return;
            if (!_settings.Director.Value || !_settings.Healing.Value || !_settings.DamageReactions.Value)
            { CancelTreatment("TreatmentDisabled"); return; }
            BarkContext context = Context;
            TreatmentSnapshot snapshot = ReadTreatmentSnapshot(context);
            bool usingMedicine = _hands is Player.MedsController meds && meds.Item is Meds;
            TreatmentOutcome outcome = _treatment.Observe(now, usingMedicine, context, snapshot, _combat.ExchangeVersion);
            if (outcome == TreatmentOutcome.None) return;
            bool requestPending = _queue.ContainsGroup("medical-request");
            string reason = outcome == TreatmentOutcome.RemainingConcern && requestPending
                ? "MedicalRequestPreferred" : "ActualHealthComparison";
            if (Tracing) Record(FormattableString.Invariant($"Event=TreatmentObservation Decision=Observed Outcome={outcome} HealthDelta={snapshot.Health - _treatment.Before.Health:F1} Reason={reason}"));
            if (outcome != TreatmentOutcome.RemainingConcern || !SemanticVoice.AfterTreatment(context, requestPending)) return;
            VoiceIntent intent = SemanticVoice.Injury(context, false);
            Offer(ReactionKind.AfterTreatment, "health", 30, 0f, 12f, _settings.InjuryRepeat.Value,
                null, InjuryPhrase(intent, context.Injury), intent);
        }

        private void CancelTreatment(string reason)
        {
            if (_treatment.Tracking && Tracing) Record($"Event=TreatmentObservation Decision=Observed Outcome=ContextChanged Reason={reason}");
            _treatment.Cancel();
            _queue.RemoveKind(ReactionKind.AfterTreatment, reason);
        }

        private void UpdateMedicalRequest(float now)
        {
            if (!_settings.Director.Value || !_settings.MedicalRequests.Value) { _medicalRequest.Pause(); return; }
            BarkContext context = Context;
            // TeamInformation also includes nearby peers behind us. A medical
            // request still needs a concrete, recently visible human recipient.
            VisibleContact recipient = MedicalRecipient(now);
            context.NearbyTeammate = recipient != null;
            MedicalSupplies supplies = MedicalSupplies.Unknown;
            if (MedicalRequestPolicy.Eligible(context) && (!_medicalRequest.Decided || _queue.ContainsGroup("medical-request")))
                supplies = ReadMedicalSupplies(now);
            else _lastMedicalScan = -1000f;
            if (_medicalRequest.Observe(now, context, supplies) && recipient != null)
            {
                _queue.RemoveKind(ReactionKind.AfterTreatment, "MedicalRequestPreferred");
                Offer(ReactionKind.MedicalRequest, "medical-request", MedicalRequestPolicy.Priority, 0.2f, 12f,
                    MedicalRequestPolicy.Repeat, recipient.Id, EPhraseTrigger.NeedMedkit, VoiceIntent.MedicalRequest);
            }
        }

        private VisibleContact MedicalRecipient(float now)
        {
            VisibleContact recipient = _ally;
            return MedicalRequestPolicy.Recipient(recipient?.Player != null,
                recipient?.Player != null && !recipient.Player.IsAI, recipient?.Alive == true, now - _lastAllySeen)
                ? recipient : null;
        }

        private MedicalSupplies ReadMedicalSupplies(float now, bool fresh = false)
        {
            if (fresh || now - _lastMedicalScan >= 1f)
            {
                bool first = _lastMedicalScan < 0f;
                _lastMedicalScan = now;
                MedicalSupplies result = _medicalSupplyProbe.Read();
                if (Tracing && (first || result != _medicalSupplies)) Record($"Event=MedicalSupplyCheck Supplies={result} Decision=Observed Reason=OwnCarriedInventory");
                _medicalSupplies = result;
            }
            return _medicalSupplies;
        }

        private bool ValidMedicalRequest(Reaction reaction, bool fresh = false)
        {
            VisibleContact recipient = MedicalRecipient(Time.time);
            if (!_settings.MedicalRequests.Value || !MedicalRequestPolicy.Eligible(Context)
                || recipient == null || recipient.Id != reaction.Subject || (fresh && !HasAlly()))
            { reaction.InvalidReason = "MedicalContextChanged"; return false; }
            MedicalSupplies supplies = ReadMedicalSupplies(Time.time, fresh);
            if (supplies == MedicalSupplies.Missing) return true;
            reaction.InvalidReason = supplies == MedicalSupplies.Available ? "MedicalSuppliesAvailable" : "MedicalSuppliesUnknown";
            return false;
        }

        private void UpdateVision(float now)
        {
            _gaze = _probe.Gaze(_player, _settings.SightRange.Value, _fika);
            _squad.Observe(now, _gaze);
            if (_gaze != null && _gaze.Ally && _gaze.Alive && _gaze.Distance <= 12f)
            {
                _ally = _gaze;
                _lastAllySeen = now;
            }
            else if (_ally != null)
            {
                var seen = _probe.Track(_player, _ally, 12f, _fika);
                if (seen != null && seen.Alive && seen.Ally) { _ally = seen; _lastAllySeen = now; }
                else if (now - _lastAllySeen > 1f) _ally = null;
            }
            if (!_settings.Visual.Value && !_settings.Combat.Value && !_squad.Enabled) return;
            VisibleContact seenEnemy = _targets.Track(now, _player, _settings.SightRange.Value, _fika, _gaze, Tracing);
            if (_gaze != null && !_gaze.Ally) Observe(_gaze, now);
            if (seenEnemy != null && !ReferenceEquals(seenEnemy, _gaze)) Observe(seenEnemy, now, focused: false);
        }

        private void Observe(VisibleContact seen, float now, bool focused = true)
        {
            if (ViewProbe.RecognizableEnemy(_player, seen))
            {
                if (Tracing && !_memory.IsHostile(seen.Id, now)) Record("Event=VisualThreat Decision=Observed Reason=VisibleFaction");
                _memory.Hostile(seen.Id, now);
            }
            if (_memory.IsHostile(seen.Id, now)) _targets.Remember(seen, now, Tracing);
            if (!seen.Alive && !seen.Ally && Tracing)
            {
                string reason = _memory.DeathBlockReason(seen.Id, now) ?? "RecentLivingWitness";
                if (now >= _deathTraceAt && (seen.Id != _deathTraceIdentity || reason != _deathTraceReason))
                {
                    _deathTraceIdentity = seen.Id; _deathTraceReason = reason; _deathTraceAt = now + 1f;
                    Record($"Event=VisualDeath Decision=Observed Corpse={seen.Corpse} Reason={reason}");
                }
            }
            var reaction = _memory.Observe(seen.Id, seen.Alive, seen.Ally, now, focused);
            if (seen.Alive && !seen.Ally && _memory.IsHostile(seen.Id, now)) _squad.Threat(now, seen, focused);
            if (!seen.Alive) _squad.TargetDown(seen.Id);
            if (!reaction.HasValue) return;
            if (reaction == ReactionKind.EnemyDown)
            {
                _combat.Defeat(now, _memory.RecentLivingThreat(now, 2f));
                _queue.RemoveSubject("contact", seen.Id, "TargetAlreadyDown");
                _queue.RemoveSubject("lost", seen.Id, "TargetAlreadyDown");
                // One contextual ending, sometimes a taunt, sometimes a report.
                // Do not keep the periodic battle chatter alive after a death.
                bool taunt = _settings.Combat.Value && _random.NextDouble() * 100 < _settings.PostKillChance.Value;
                VoiceIntent intent = SemanticVoice.EnemyDown(Context, taunt);
                if (!_settings.Visual.Value) return;
                if (intent == VoiceIntent.Silent) _queue.RemoveGroup("kill", "NewObservedDown");
                else
                {
                    // Selection is distinct from playback history: an absent clip
                    // must not cause a different closing line to fill its place.
                    _combat.SelectEnding();
                    _queue.RemoveGroup("recovery", "EndingSelected");
                }
                Offer(reaction.Value, "kill", 78, 0.8f + (float)_random.NextDouble() * 1.6f,
                    7f, 7f, seen.Id, intent == VoiceIntent.KillTaunt ? EPhraseTrigger.OnFight : EPhraseTrigger.EnemyDown, intent);
            }
            else
            {
                _queue.RemoveSubject("lost", seen.Id, "TargetReacquired");
                if (_settings.Visual.Value) Offer(reaction.Value, "contact", 80, 0.25f, 5f, 10f, seen.Id,
                    reaction == ReactionKind.RepeatedContact ? EPhraseTrigger.OnRepeatedContact : EPhraseTrigger.OnFirstContact);
            }
        }

        private void OnShot()
        {
            Guard(() =>
            {
                if (!Collect) return;
                float now = Time.time;
                _lastShot = now;
                // Sample the actual shot direction now; a cached target from
                // before a flick or turn must not turn a test shot into combat.
                var target = _probe.Gaze(_player, _settings.SightRange.Value, _fika);
                bool visible = target != null && target.Alive && !target.Ally;
                bool justKilled = target != null && !target.Alive && _gaze != null && _gaze.Alive && !_gaze.Ally
                    && target.Id == _gaze.Id && now - _lastVision <= 0.25f;
                _squad.Shot(now, target);
                _combat.Shot(now, visible || justKilled, _memory.RecentLivingThreat(now, 8f));
                if (_combat.Active(now)) CancelTreatment("CombatResumed");
                if (visible || justKilled)
                {
                    _memory.Hostile(target.Id, now);
                    Observe(target, now);
                    _gaze = target;
                }
            });
        }

        private void OnHit(DamageInfo info, EBodyPart part, float absorbed)
        {
            Guard(() =>
            {
                if (!Collect || !IsCombatDamage(info.DamageType) || info.Damage <= 0f) return;
                FlushDamage(Time.time);
                _lastDamage = Time.time;
                _damageVersion++;
                CancelTreatment("DamageAfterTreatment");
                _queue.RemoveKind(ReactionKind.Recovery, "DamageAfterRequest");
                _queue.RemoveGroup("kill", "DamageAfterRequest");
                _rawHits++;
                IPlayer attacker = info.Player?.iPlayer;
                bool friendly = attacker != null && ViewProbe.IsAlly(_player, attacker);
                if (!friendly && (attacker == null || attacker.ProfileId != _player.ProfileId)) _combat.Incoming(Time.time);
                if (attacker != null && !friendly && attacker.ProfileId != _player.ProfileId)
                    _memory.Hostile(attacker.ProfileId, Time.time);
                if (!_settings.DamageReactions.Value) return;
                _damage.Add(Time.time, info.Damage, HealthLevel(), friendly);
            });
        }

        private void FlushDamage(float now)
        {
            if (!_damage.Ready(now)) return;
            float remaining = 7f - (now - _damage.StartedAt);
            if (_settings.DamageReactions.Value && remaining > 0f)
            {
                // Re-read health after the burst rather than freezing the first impact.
                InjuryLevel severity = (InjuryLevel)Math.Max((int)_damage.Severity, (int)HealthLevel());
                VoiceIntent intent = SemanticVoice.Injury(Context, _damage.FriendlyOnly);
                EPhraseTrigger phrase = InjuryPhrase(intent, severity);
                if (Tracing) Record(FormattableString.Invariant($"Event=DamageAggregate Hits={_damage.Hits} ReportedDamage={_damage.Damage:F1} Severity={severity}"));
                Offer(ReactionKind.Injury, "health", SemanticVoice.InjuryPriority(severity, Context.HeavyBleeding),
                    0f, remaining, _settings.InjuryRepeat.Value, null, phrase, intent, _damage.FriendlyOnly);
            }
            _damage.Clear();
        }

        private EPhraseTrigger InjuryPhrase()
        {
            return InjuryPhrase(HealthLevel());
        }

        private static EPhraseTrigger InjuryPhrase(InjuryLevel severity)
        {
            switch (severity)
            {
                case InjuryLevel.Dying: return EPhraseTrigger.HurtNearDeath;
                case InjuryLevel.BadlyInjured: return EPhraseTrigger.HurtHeavy;
                case InjuryLevel.Injured: return EPhraseTrigger.HurtMedium;
                default: return EPhraseTrigger.UnderFire;
            }
        }

        private static EPhraseTrigger InjuryPhrase(VoiceIntent intent, InjuryLevel severity)
        {
            switch (intent)
            {
                case VoiceIntent.Bleeding: return EPhraseTrigger.Bleeding;
                case VoiceIntent.ArmFracture: return EPhraseTrigger.HandBroken;
                case VoiceIntent.LegFracture: return EPhraseTrigger.LegBroken;
                case VoiceIntent.FriendlyFire: return EPhraseTrigger.FriendlyFire;
                default: return InjuryPhrase(severity);
            }
        }

        internal static bool IsCombatDamage(EDamageType type)
        {
            return type == EDamageType.Bullet || type == EDamageType.GrenadeFragment || type == EDamageType.Explosion
                || type == EDamageType.Sniper || type == EDamageType.Melee || type == EDamageType.Blunt;
        }

        private void OnNativeSituation(EPhraseTrigger phrase, int seconds)
        {
            Guard(() =>
            {
                if (!Collect || !_settings.AutoContext.Value) return;
                float ttl = Math.Min(8f, Math.Max(1f, seconds));
                switch (phrase)
                {
                    case EPhraseTrigger.OnWeaponReload:
                        _reloadDecision.Native(Time.time);
                        QueueReload();
                        break;
                    case EPhraseTrigger.NeedAmmo:
                        Offer(ReactionKind.Ammo, "ammo", 96, 0.2f, ttl, 8f, WeaponId, phrase);
                        break;
                    case EPhraseTrigger.OnWeaponJammed: case EPhraseTrigger.WeaponBroken: case EPhraseTrigger.OnWeaponMisfired:
                        if (_gun == null || !ReferenceEquals(_player.HandsController, _gun) || !WeaponFaulty) break;
                        _combat.Malfunction(Time.time);
                        int episode = _malfunctions.Begin(WeaponId, true);
                        if (episode != 0)
                            Offer(ReactionKind.Malfunction, "malfunction", 96, 0.25f, ttl, 8f, WeaponId, phrase,
                                malfunctionEpisode: episode);
                        break;
                    case EPhraseTrigger.OnEnemyGrenade:
                        _queue.RemoveGroup("grenade-out", "GrenadeDanger");
                        Offer(ReactionKind.Grenade, "grenade-warning", SemanticVoice.GrenadeWarningPriority,
                            0.15f, Math.Min(ttl, 3f), 2f, null, phrase);
                        break;
                    case EPhraseTrigger.OnGrenade:
                        if (!_queue.ContainsGroup("grenade-warning"))
                            Offer(ReactionKind.Grenade, "grenade-out", SemanticVoice.GrenadeOutPriority,
                                0.15f, Math.Min(ttl, 3f), 2f, null, phrase);
                        break;
                }
                // Native OnEnemyDown/OnEnemyShot can be generated through walls.
                // They intentionally do not authorize a visual reaction.
            });
        }

        private void QueueReload()
        {
            if (!_reloadDecision.Begin()) return;
            BarkContext context = Context;
            double roll = _random.NextDouble();
            float calmChance = _settings.CalmReloadChance.Value, recoveryChance = _settings.RecoveryReloadChance.Value;
            float chance = SemanticVoice.ReloadChance(context, calmChance, recoveryChance);
            bool selected = SemanticVoice.Reload(context, roll, calmChance, recoveryChance);
            int requestId = _journal.Request();
            if (Tracing) Record(FormattableString.Invariant($"Event=ReloadRoll Request={requestId} Voice={_player.Speaker.PlayerVoice} Mode={context.Mode} Combat={context.Combat} Phrase=OnWeaponReload ChancePercent={chance:F3} RollPercent={roll * 100d:F3} Passed={selected}"));
            VoiceIntent intent = selected
                ? IntentFor(ReactionKind.Reload, EPhraseTrigger.OnWeaponReload) : VoiceIntent.Silent;
            Offer(ReactionKind.Reload, "reload", SemanticVoice.ReloadPriorityFor(context), 0.15f, 8f,
                _settings.ReloadRepeat.Value, WeaponId, EPhraseTrigger.OnWeaponReload, intent, requestId: requestId);
        }

        private void Offer(ReactionKind kind, string group, int priority, float delay, float ttl,
            float repeat, string subject, EPhraseTrigger phrase, VoiceIntent intent = VoiceIntent.None,
            bool friendlyDamage = false, int malfunctionEpisode = 0, SquadDecision squad = default, SquadLoss loss = default, int requestId = 0, int npcEpisode = 0)
        {
            if (!_settings.Director.Value) return;
            float now = Time.time;
            if (intent == VoiceIntent.None) intent = IntentFor(kind, phrase);
            if (intent == VoiceIntent.KillTaunt) priority = SemanticVoice.TauntPriority;
            var reaction = new Reaction { Kind = kind, Priority = priority, ReadyAt = now + delay,
                ExpiresAt = now + ttl, Subject = subject, RequiresEmpty = phrase == EPhraseTrigger.OnOutOfAmmo,
                DamageVersion = _damageVersion, ExchangeVersion = _combat.ExchangeVersion, MalfunctionEpisode = malfunctionEpisode,
                TreatmentVersion = _treatment.Version,
                NpcEpisode = npcEpisode,
                Squad = squad, Loss = loss,
                FriendlyDamage = friendlyDamage,
                Cue = new VoiceCue { RequestId = requestId == 0 ? _journal.Request() : requestId, Phrase = (int)phrase, Key = group + ":" + (subject ?? phrase.ToString()), Group = group,
                    Urgent = priority >= 82, Repeat = repeat, GroupGap = repeat, Intent = intent,
                    SemanticRepeat = intent == VoiceIntent.KillTaunt || intent == VoiceIntent.NpcRetort ? _settings.CombatMin.Value : 0f } };
            _generated[(int)kind]++;
            if (intent == VoiceIntent.Silent) { Dropped(reaction, kind == ReactionKind.Reload ? "ReloadProbability" : "ContextPolicy"); return; }
            if (group == "health")
            {
                _healthSpeech.Observe(now, Context);
                if (kind == ReactionKind.Recovery && _healthSpeech.Considered)
                { Dropped(reaction, "SameHealthEpisode"); return; }
                // Only a fresh fact reopens the episode. Merely queueing or
                // choosing a phrase must not consume a successful expression.
                if (kind == ReactionKind.Injury || kind == ReactionKind.AfterTreatment) _healthSpeech.BeginEvent();
                RefreshHealthCue(reaction, Context);
            }
            Trace(reaction, "Candidate", "Event");
            if (kind == ReactionKind.EnemyDown) _queue.OfferEnding(reaction, now);
            else _queue.Offer(reaction, now);
        }

        private VoiceIntent IntentFor(ReactionKind kind, EPhraseTrigger phrase)
        {
            switch (kind)
            {
                case ReactionKind.Reload: return Context.TeamInformation && _combat.Active(Time.time)
                    ? VoiceIntent.ReloadTactical : VoiceIntent.ReloadAction;
                case ReactionKind.Ammo: return phrase == EPhraseTrigger.OnOutOfAmmo ? VoiceIntent.EmptyWeapon : VoiceIntent.NeedAmmo;
                case ReactionKind.Malfunction: return VoiceIntent.Malfunction;
                case ReactionKind.Grenade: return phrase == EPhraseTrigger.OnEnemyGrenade ? VoiceIntent.GrenadeWarning : VoiceIntent.GrenadeOut;
                case ReactionKind.Injury: return phrase == EPhraseTrigger.FriendlyFire ? VoiceIntent.FriendlyFire : VoiceIntent.Injury;
                case ReactionKind.Heal: return VoiceIntent.Healing;
                case ReactionKind.Contact: return VoiceIntent.Contact;
                case ReactionKind.RepeatedContact: return VoiceIntent.RepeatedContact;
                case ReactionKind.LostVisual: return VoiceIntent.LostVisual;
                case ReactionKind.Recovery: return VoiceIntent.Recovery;
                case ReactionKind.Reply: return VoiceIntent.Acknowledge;
                default: return VoiceIntent.KillConfirm;
            }
        }

        public Reaction Next(float now, VoicePolicy policy, bool aiming, bool stealth, bool sprinting, Func<VoiceCue, string> nativeGate)
        {
            if (!_settings.Director.Value) return null;
            return _queue.Next(now, Valid, r =>
            {
                string blocked = SemanticVoice.WaitingReason(r.Cue.Intent, now - _lastDamage < SemanticVoice.RecentHitWindow)
                    ?? nativeGate(r.Cue) ?? policy.BlockReason(r.Cue, now);
                if (blocked != null) { Waiting(r, blocked); return false; }
                if (r.Kind == ReactionKind.NpcReply && (blocked = _npc.Waiting(now)) != null)
                { Waiting(r, blocked); return false; }
                if (!r.Cue.Urgent && OthersSpeaking(now)) { Waiting(r, "NearbySpeech"); return false; }
                blocked = PlaybackRules.Movement(r.Cue.Intent, r.Cue.Urgent, aiming, sprinting,
                    stealth && _settings.RespectStealth.Value);
                if (blocked != null) { Waiting(r, blocked); return false; }
                return true;
            });
        }

        private bool Valid(Reaction r)
        {
            if (!Collect) return false;
            BarkContext context = Context;
            if (r.Cue.Group == "health") RefreshHealthCue(r, context);
            r.InvalidReason = SemanticVoice.PendingRejectionReason(r.Cue.Intent, context, r.DamageVersion);
            if (r.InvalidReason != null) return false;
            if ((r.Kind == ReactionKind.EnemyDown || r.Kind == ReactionKind.Recovery || r.Kind == ReactionKind.AfterTreatment)
                && r.ExchangeVersion != _combat.ExchangeVersion)
            { r.InvalidReason = "ExchangeResumed"; return false; }
            switch (r.Kind)
            {
                case ReactionKind.Reload:
                    r.Priority = SemanticVoice.ReloadPriorityFor(Context);
                    r.Cue.Urgent = r.Priority >= 82;
                    return _settings.AutoContext.Value && _gun != null && WeaponId == r.Subject && _reloadDecision.Valid(Time.time);
                case ReactionKind.Ammo: return _settings.AutoContext.Value && _gun != null && WeaponId == r.Subject && !_reload
                    && (!r.RequiresEmpty || _empty);
                case ReactionKind.Malfunction: return _settings.AutoContext.Value && WeaponId == r.Subject
                    && WeaponFaulty && _malfunctions.Matches(r.Subject, r.MalfunctionEpisode);
                case ReactionKind.Grenade: return _settings.AutoContext.Value;
                case ReactionKind.Injury:
                    r.Priority = SemanticVoice.InjuryPriority(HealthLevel(), Context.HeavyBleeding);
                    return _settings.DamageReactions.Value && !(_hands is Player.MedsController);
                case ReactionKind.Heal: return _settings.Healing.Value && _hands is Player.MedsController meds && meds.Item is Meds;
                case ReactionKind.Contact: case ReactionKind.RepeatedContact:
                    r.InvalidReason = _settings.Visual.Value ? _memory.ContactBlockReason(r.Subject, Time.time) : "VisualDisabled";
                    return r.InvalidReason == null;
                case ReactionKind.EnemyDown: return _settings.Visual.Value && _memory.DeathConfirmed(r.Subject);
                case ReactionKind.LostVisual: return _settings.Visual.Value && _combat.Active(Time.time) && _memory.LostStillValid(r.Subject, Time.time);
                case ReactionKind.Recovery: return _settings.Idle.Value && SemanticVoice.Recovery(Context, Time.time < r.ReadyAt)
                    && r.DamageVersion == _damageVersion
                    && (r.Cue.Group == "health" ? _settings.DamageReactions.Value && Context.Wounded : !Context.Wounded);
                case ReactionKind.Reply: return _settings.TeamReplies.Value && HasAlly() && _ally.Id == r.Subject;
                case ReactionKind.MedicalRequest: return ValidMedicalRequest(r);
                case ReactionKind.AfterTreatment: return ValidAfterTreatment(r);
                case ReactionKind.Squad: return ValidSquad(r);
                case ReactionKind.FriendlyDown: return !Context.Survival && _squad.Valid(r.Loss, Time.time);
                case ReactionKind.NpcReply:
                    r.InvalidReason = _settings.NpcReplies.Value && _npc.Valid(r.NpcEpisode, Time.time)
                        ? null : "NpcExchangeEnded";
                    return r.InvalidReason == null;
                default: return false;
            }
        }

        private bool ValidAfterTreatment(Reaction reaction)
        {
            BarkContext context = Context;
            if (!_settings.Healing.Value || !_settings.DamageReactions.Value
                || reaction.TreatmentVersion != _treatment.Version || reaction.DamageVersion != _damageVersion
                || reaction.ExchangeVersion != _combat.ExchangeVersion
                || !SemanticVoice.AfterTreatment(context, _queue.ContainsGroup("medical-request")))
            { reaction.InvalidReason = "TreatmentContextChanged"; return false; }
            TreatmentSnapshot current = ReadTreatmentSnapshot(context);
            if (!current.Valid || current.NewConcernSince(_treatment.After))
            { reaction.InvalidReason = "NewConditionAfterTreatment"; return false; }
            return true;
        }

        private bool ValidSquad(Reaction reaction)
        {
            BarkContext context = Context;
            reaction.InvalidReason = !SquadMemory.Allowed(reaction.Squad.Action, context) ? "SquadSurvival"
                : _squad.RejectionReason(reaction.Squad, Time.time, CoverWeaponReady);
            return reaction.InvalidReason == null;
        }

        private static void RefreshHealthCue(Reaction reaction, BarkContext context)
        {
            reaction.Cue.Intent = SemanticVoice.Injury(context, reaction.FriendlyDamage);
            int phrase = (int)InjuryPhrase(reaction.Cue.Intent, context.Injury);
            if (reaction.Cue.Phrase != phrase)
            {
                reaction.Cue.Phrase = phrase;
                reaction.Cue.Key = "health:" + (EPhraseTrigger)phrase;
            }
            reaction.Cue.AcuteHealth = reaction.Kind == ReactionKind.Injury;
            reaction.Cue.HealthSeverity = context.Injury;
            reaction.Cue.HeavyBleeding = context.HeavyBleeding;
        }

        public bool HasAlly()
        {
            if (_ally == null || Time.time - _lastAllySeen > 1f) return false;
            var seen = _probe.Track(_player, _ally, 12f, _fika);
            return seen != null && seen.Ally && seen.Alive;
        }

        public bool Choose(Reaction reaction)
        {
            BarkContext context = Context;
            reaction.InvalidReason = SemanticVoice.RejectionReason(reaction.Cue.Intent, context, reaction.DamageVersion);
            if (reaction.InvalidReason != null) return false;
            if ((reaction.Kind == ReactionKind.Contact || reaction.Kind == ReactionKind.RepeatedContact)
                && !Valid(reaction)) return false;
            if (reaction.Kind == ReactionKind.MedicalRequest && !ValidMedicalRequest(reaction, fresh: true)) return false;
            if (reaction.Kind == ReactionKind.AfterTreatment && !ValidAfterTreatment(reaction)) return false;
            if (reaction.Kind == ReactionKind.Squad && !ValidSquad(reaction)) return false;
            if (reaction.Cue.Intent == VoiceIntent.KillTaunt && !_settings.Combat.Value)
            {
                reaction.InvalidReason = "CombatDisabled";
                return false;
            }
            if (reaction.Kind == ReactionKind.Injury || reaction.Kind == ReactionKind.AfterTreatment
                || (reaction.Kind == ReactionKind.Recovery && reaction.Cue.Group == "health"))
            {
                // Re-evaluate actual health, then request only that meaning.
                // Bank availability must never choose a different injury.
                RefreshHealthCue(reaction, context);
                // The state may have worsened while this request was waiting.
                _healthSpeech.Observe(Time.time, context);
                if (_healthSpeech.Disposition == HealthDisposition.RetryLimit)
                { reaction.InvalidReason = "HealthPlaybackRetryLimit"; return false; }
                if (!_healthSpeech.MatchesSelection(reaction.Cue.Phrase))
                { reaction.InvalidReason = "HealthPhraseAlreadySelected"; return false; }
            }
            if (_hasClip((EPhraseTrigger)reaction.Cue.Phrase)) return true;
            if (reaction.Cue.Group == "health") _healthSpeech.Missing();
            reaction.InvalidReason = "NoMatchingBank";
            return false;
        }

        public bool Reserve(float now) => _queue.Reserve(now) || _npc.Pending;
        public void DiscardPending()
        {
            _queue.Clear(); _damage.Clear(); CancelTreatment("ManualInput");
            _squad.Pause(Time.time, manual: true);
            _npc.Pause();
        }
        public void Completed(Reaction r, bool played)
        {
            if (played) { _queue.Remove(r); _played[(int)r.Kind]++; Trace(r, "Played", "SpeakerConfirmed"); }
            else { r.ReadyAt = Time.time + 1.25f; Waiting(r, "NativePlaybackRejected"); }
        }
        public void Unavailable(Reaction r) { _queue.Remove(r); Dropped(r, r.InvalidReason ?? "NoMatchingBank"); }
        public void Spoke(VoiceCue cue, bool automatic)
        {
            if (cue == null) return;
            if (WoundCue(cue)) _healthSpeech.Consider();
            if (cue.Group == "reload") _reloadDecision.Spoken();
            if (cue.Group == "malfunction") _malfunctions.Begin(WeaponId, WeaponFaulty);
            if (cue.Group == "medical-request") _medicalRequest.Spoken();
            if (cue.Intent == VoiceIntent.NpcRetort) _npc.Spoken(Time.time, _settings.NpcReplyCooldown.Value);
            if ((cue.Group == "team-coordination" || cue.Group == "team-response" || cue.Group == "team-retreat" || cue.Group == "team-request")
                && !automatic) _squad.Pause(Time.time, manual: true);
            _queue.RemoveGroup(cue.Group, automatic ? "SpeechObserved" : "NativeOrManualSpeech");
        }

        private void Waiting(Reaction r, string reason)
        {
            if (r.LastWaitReason == reason) return;
            r.LastWaitReason = reason;
            Trace(r, "Waiting", reason);
        }

        private void Dropped(Reaction r, string reason)
        {
            if (reason == "SpeechObserved") return;
            if (r.Kind == ReactionKind.NpcReply && r.NpcEpisode == _npc.Version)
                _npc.Cancel(reason, Tracing);
            if (reason == "ContextInvalid" && r.InvalidReason != null) reason = r.InvalidReason;
            _suppressed[(int)r.Kind]++;
            Trace(r, "Suppressed", reason);
        }

        private void Trace(Reaction r, string decision, string reason)
        {
            if (!_settings.Debug.Value && !_journal.Enabled) return;
            BarkContext c = Context;
            string fields = FormattableString.Invariant($"Event={r.Kind} Request={r.Cue.RequestId} Priority={r.Priority} Group={r.Cue.Group} Remaining={r.ExpiresAt - Time.time:F2} Voice={_player?.Speaker?.PlayerVoice} Mode={c.Mode} Combat={c.Combat} FightSeconds={c.FightDuration:F1} PeakPressure={c.PeakPressure:F1} TookHostileHit={c.TookHostileHit} HadMalfunction={c.HadMalfunction} ObservedDowns={c.RecentObservedDowns} EndingSelected={c.EndingSelected} Exchange={r.ExchangeVersion}/{_combat.ExchangeVersion} FaultEpisode={r.MalfunctionEpisode} Health={c.Injury} HealthEpisode={_healthSpeech.Version} Bleeding={c.Bleeding} HeavyBleeding={c.HeavyBleeding} ArmFracture={c.ArmFracture} LegFracture={c.LegFracture} RecentHit={c.RecentlyHit} DamageVersion={r.DamageVersion}/{c.DamageVersion} Healing={c.Healing} Teammate={c.NearbyTeammate} RecentTeammate={c.RecentTeammate} AcuteHealth={r.Cue.AcuteHealth} Intent={r.Cue.Intent} Phrase={(EPhraseTrigger)r.Cue.Phrase} SquadAction={r.Squad.Action} Peer={(r.Squad.Exists ? r.Squad.Slot : r.Loss.Exists ? r.Loss.Slot : -1)} PeerVersion={(r.Squad.Exists ? r.Squad.PeerVersion : r.Loss.Version)} Episode={r.Squad.Episode} Reply={r.Squad.Reply} NpcEpisode={r.NpcEpisode} Decision={decision} Reason={reason}");
            Record(fields);
        }

        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                _faulted = true;
                if (Tracing) Record("Event=Fault Component=Director Reason=" + ex.GetType().Name);
                Suspend();
                _log.LogError("Event voice disabled for this raid after callback error: " + ex.Message);
            }
        }

        public void Suspend()
        {
            _collecting = false;
            _healthSpeech.Pause();
            _queue.Clear(); _memory.Clear(); _combat.Clear();
            _damage.Clear(); _lastDamage = -1000f;
            _treatment.Clear();
            _medicalRequest.Pause(); _lastMedicalScan = -1000f; _medicalSupplies = MedicalSupplies.Unknown;
            _gaze = _ally = null; _targets.Clear();
            _deathTraceIdentity = _deathTraceReason = null;
            _squad.Pause(Time.time);
            _npc.Pause();
            _recoveryVersion = -1;
            _lastShot = -100f;
            _lastVision = _lastAllySeen = -100f;
            if (_gun != null) _gun.OnShot -= OnShot;
            _gun = null; _hands = null;
            _reload = _empty = false; _reloadDecision.Clear();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Suspend();
            _malfunctions.Clear();
            _squad.Dispose();
            if (_gun != null) _gun.OnShot -= OnShot;
            _player.PhraseSituation -= OnNativeSituation;
            _player.BeingHitAction -= OnHit;
            if (_settings.Debug.Value || _journal.Enabled)
            {
                Record($"Event=DirectorSummary RawCombatDamageCallbacks={_rawHits}");
                for (int i = 0; i < _generated.Length; i++)
                {
                    string fields = $"Event=EventSummary Kind={(ReactionKind)i} Candidates={_generated[i]} Played={_played[i]} Suppressed={_suppressed[i]}";
                    Record(fields);
                }
            }
        }
    }
}
