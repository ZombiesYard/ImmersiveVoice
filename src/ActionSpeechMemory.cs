namespace ImmersiveVoice
{
    internal enum SearchSpeechTarget { None, Corpse, Container }

    // The caller opens this only from a newly-added local SearchContentOperation.
    // Inventory panels, item transfers and proximity never enter this state.
    internal sealed class SearchSpeechMemory
    {
        public const float ReactionDelay = .35f, OpportunityWindow = 4f;
        private long _operation, _lastOperation;
        private float _started;
        private bool _active, _decided;
        public int Version { get; private set; }
        public string Reason { get; private set; }
        public bool Active => _active;

        public bool Begin(long operation, SearchSpeechTarget target, float now)
        {
            if (operation <= 0 || target == SearchSpeechTarget.None || operation == _lastOperation) return false;
            if (_active) Cancel("SearchReplaced");
            Version++; _operation = _lastOperation = operation; _started = now;
            _active = true; _decided = false; Reason = "SearchStarted";
            return true;
        }

        public int Take(float now)
        {
            if (!_active || _decided) return 0;
            if (now >= _started + OpportunityWindow) { Cancel("SearchExpired"); return 0; }
            if (now < _started + ReactionDelay) return 0;
            _decided = true; Reason = "SearchSelected"; return Version;
        }

        public bool Valid(int version, float now) => _active && _decided && version == Version
            && now < _started + OpportunityWindow;

        public void End(long operation, bool completed)
        {
            if (!_active || operation != _operation) return;
            Cancel(completed ? "SearchEnded" : "SearchCancelled");
        }

        public void Danger() { if (_active) Cancel("DangerDuringSearch"); }
        public void Pause() { if (_active) Cancel("SearchPaused"); }
        public void Clear() { Cancel("SearchCleared"); _lastOperation = 0; }
        private void Cancel(string reason)
        {
            Version++; _operation = 0; _active = false; _decided = false; Reason = reason;
        }
    }

    internal struct GeneralHelpState
    {
        public bool WeaponPresent, WeaponReady, Reloading, Faulty, Operating;
        public bool AmmoKnown, NearbyHuman;
        public int Ammo;
        public bool PreciseCover, PreciseMedkit, PreciseAmmo;
        public bool TemporarilyUnavailable => WeaponPresent && !WeaponReady && (Reloading || Faulty || Operating);
        public bool MorePrecise => PreciseCover || PreciseMedkit || PreciseAmmo || AmmoKnown && Ammo <= 0;
    }

    // One broad request for one continuous hostile-impact cluster. Exact CoverMe,
    // NeedMedkit and NeedAmmo meanings consume the opportunity before NeedHelp.
    internal sealed class GeneralHelpMemory
    {
        public const float DangerWindow = 5f, ClusterGap = 8f;
        private float _dangerAt = -1000f;
        private int _episode;
        private bool _decided, _offered;
        public int Version { get; private set; }
        public string Reason { get; private set; }

        // eventId is a local monotonically increasing hostile-damage sequence.
        public bool Danger(float now, int eventId)
        {
            if (eventId <= 0) return false;
            if (now - _dangerAt > ClusterGap)
            {
                _episode++; _decided = _offered = false;
            }
            _dangerAt = now; Reason = "HostileDangerObserved";
            return true;
        }

        public int Take(float now, GeneralHelpState state)
        {
            if (_decided || now - _dangerAt > DangerWindow) return 0;
            if (state.MorePrecise)
            {
                _decided = true; Reason = "MorePreciseRequest"; return 0;
            }
            if (!state.TemporarilyUnavailable || !state.NearbyHuman) return 0;
            _decided = _offered = true; Version++; Reason = "GeneralHelpSelected";
            return Version;
        }

        public bool Valid(int version, float now, GeneralHelpState state) => _offered && version == Version
            && now - _dangerAt <= DangerWindow && state.TemporarilyUnavailable
            && state.NearbyHuman && !state.MorePrecise;

        public void Pause() { _decided = true; _offered = false; Version++; Reason = "HelpPaused"; }
        public void Clear()
        {
            _dangerAt = -1000f; _episode = 0; _decided = _offered = false;
            Version++; Reason = "HelpCleared";
        }
    }

    internal struct TreatmentReadyState
    {
        public TreatmentSnapshot Health;
        public int DamageVersion;
        public bool OperationActive, CanAct, NearbyHuman;
    }

    // A Ready line closes a confirmed local medicine operation. Improvement,
    // successful completion, restored control and a remaining human audience are
    // separate requirements. A continuation remains in the same bounded segment.
    internal sealed class TreatmentReadyMemory
    {
        public const float SettleDelay = .4f, CompletionGrace = 2f, ContinuationGap = 1.25f;
        public const float MaximumWindow = 90f, PlaybackWindow = 5f;
        private TreatmentSnapshot _before, _latest;
        private long _operation;
        private int _damage;
        private float _started, _ended = -1f;
        private bool _tracking, _operationActive, _improved, _decided;
        public int Version { get; private set; }
        public string Reason { get; private set; }

        public bool Begin(long operation, float now, TreatmentReadyState state)
        {
            if (operation <= 0 || !state.OperationActive || !state.Health.Valid) return false;
            if (_tracking && _operationActive && operation == _operation) return false;
            if (_tracking && !_operationActive && now - _started < MaximumWindow
                && _ended >= 0f && now - _ended <= ContinuationGap)
            {
                Version++; _operation = operation; _operationActive = true; _ended = -1f;
                _latest = state.Health; _improved |= state.Health.ImprovedFrom(_before);
                Reason = "TreatmentContinued";
                return true;
            }
            Cancel("TreatmentReplaced");
            Version++; _tracking = _operationActive = true; _operation = operation;
            _before = _latest = state.Health; _damage = state.DamageVersion;
            _started = now; _ended = -1f; _improved = _decided = false;
            Reason = "TreatmentStarted";
            return true;
        }

        public bool End(long operation, float now, bool completed, TreatmentReadyState state)
        {
            if (!_tracking || !_operationActive || operation != _operation) return false;
            if (!completed) { Cancel("TreatmentCancelled"); return true; }
            if (!ObserveState(now, state)) return true;
            _operationActive = false; _ended = now; Reason = "TreatmentCompleted";
            return true;
        }

        public void Observe(float now, TreatmentReadyState state)
        {
            if (!_tracking || !ObserveState(now, state)) return;
            if (_operationActive && !state.OperationActive) { Cancel("TreatmentEndUnconfirmed"); return; }
            if (!_operationActive && state.OperationActive) { Cancel("TreatmentContinuationUntracked"); return; }
            if (_operationActive) return;
            if (!state.NearbyHuman) { Cancel("TreatmentAudienceLost"); return; }
            if (now - _ended > CompletionGrace && (!_improved || !state.CanAct))
                Cancel(!_improved ? "TreatmentNotImproved" : "TreatmentStillBlocked");
        }

        public int Take(float now, TreatmentReadyState state)
        {
            Observe(now, state);
            if (!_tracking || _operationActive || _decided || _ended < 0f || !_improved
                || !state.CanAct || !state.NearbyHuman || now < _ended + SettleDelay) return 0;
            if (now >= _ended + PlaybackWindow) { Cancel("TreatmentReadyExpired"); return 0; }
            _decided = true; Reason = "TreatmentReadySelected"; return Version;
        }

        public bool Valid(int version, float now, TreatmentReadyState state)
            => _tracking && !_operationActive && _decided && version == Version && _improved
                && now < _ended + PlaybackWindow && state.Health.Valid
                && state.DamageVersion == _damage && !state.Health.NewConcernSince(_before)
                && !state.OperationActive && state.CanAct && state.NearbyHuman;

        public void Pause() { if (_tracking) Cancel("TreatmentPaused"); }
        public void Clear() { Cancel("TreatmentCleared"); _before = _latest = default; }

        private bool ObserveState(float now, TreatmentReadyState state)
        {
            if (now - _started >= MaximumWindow) { Cancel("TreatmentExpired"); return false; }
            if (!state.Health.Valid || state.DamageVersion != _damage || state.Health.NewConcernSince(_before))
            { Cancel("TreatmentContextChanged"); return false; }
            _latest = state.Health; _improved |= state.Health.ImprovedFrom(_before);
            return true;
        }

        private void Cancel(string reason)
        {
            Version++; _tracking = _operationActive = false; _operation = 0;
            _ended = -1f; _improved = _decided = false; Reason = reason;
        }
    }
}
