using System;

namespace ImmersiveVoice
{
    internal struct ReadyWeaponState
    {
        public string Weapon, Magazine;
        public int Ammo, Damage;
        public bool Reloading, Faulty, Operating, Blocked;
        public bool Busy => Reloading || Faulty || Operating;
        public bool Usable => Weapon != null && !Busy && Ammo > 0 && !Blocked;
    }

    // One local action, one exact cover conversation, one closing attempt.
    // No roster scans, clocks, allocations, or remote weapon inference here.
    internal sealed class SquadReadyMemory
    {
        private ReadyWeaponState _state, _baseline;
        private string _weapon;
        private int _damage;
        private bool _reload, _fault, _offered;
        private float _started, _completed = -1f, _coverEnd;
        public DialogueTicket Ticket { get; private set; }
        public int Version { get; private set; }
        public int CoverActor { get; private set; }
        public string Reason { get; private set; }
        public bool Active => Ticket.Exists;
        public float ExpiresAt => _completed < 0 ? _started + 25f : _completed + 5f;

        public void Observe(ReadyWeaponState state, float now)
        {
            if (state.Weapon != _state.Weapon || state.Busy && !_state.Busy) _baseline = _state.Weapon == state.Weapon ? _state : state;
            _state = state;
            if (!Active) return;
            string invalid = state.Weapon != _weapon ? "WeaponChanged" : state.Damage != _damage ? "NewDamage"
                : state.Blocked ? "SurvivalOrTreatment" : now >= ExpiresAt ? "ReadyExpired"
                : _completed >= 0 && (state.Busy || !state.Usable) ? "WeaponNoLongerReady" : null;
            if (invalid != null) { Cancel(invalid); return; }
            if (_completed >= 0) return;
            _reload |= state.Reloading; _fault |= state.Faulty;
            if (state.Busy || !(_reload || _fault)) return;
            bool changed = _fault || state.Ammo > _baseline.Ammo
                || state.Magazine != null && state.Magazine != _baseline.Magazine;
            if (!state.Usable || !changed) { Cancel("NoUsableActionResult"); return; }
            _completed = now;
            Reason = "WeaponRecovered";
        }
        public bool Arm(DialogueTicket ticket, int phrase, float now)
        {
            if (!ticket.Exists || !SquadDialogue.ReadyCall(phrase) || _state.Weapon == null || _state.Blocked
                || !(_state.Reloading || _state.Faulty || phrase == 20 && _state.Ammo == 0)) return false;
            // Repeated native requests during one action must not steal a heard
            // cover or create another closing. A different call cancels safely.
            Cancel("NewCoverCall");
            Ticket = ticket; _weapon = _state.Weapon; _damage = _state.Damage;
            _started = now; _completed = -1f; _reload = _state.Reloading; _fault = _state.Faulty;
            _offered = false; CoverActor = 0; Reason = "AwaitingHeardCover";
            return true;
        }
        public bool Covered(DialogueTicket ticket, int actor, float heardAt, float duration, float now)
        {
            if (!Active || !Ticket.Same(ticket) || actor <= 0 || actor == ticket.Caller || CoverActor != 0
                || heardAt < _started || heardAt > now || now - heardAt > 2f
                || _completed >= 0 && heardAt > _completed || now >= ExpiresAt
                || float.IsNaN(duration) || duration <= 0 || duration > 20) return false;
            CoverActor = actor; _coverEnd = heardAt + duration + .4f; Reason = "HeardCoverConfirmed";
            return true;
        }
        public int Take(float now)
        {
            if (!Active || _offered || CoverActor == 0 || _completed < 0 || now < Math.Max(_coverEnd, _completed + .4f)
                || now >= ExpiresAt || !_state.Usable) return 0;
            _offered = true; Reason = "ClosingSelected"; return Version;
        }
        public bool Valid(int version, float now) => Active && Version == version && _offered
            && CoverActor > 0 && _completed >= 0 && now < ExpiresAt && _state.Usable;
        public void Cancel(string reason)
        {
            Version++; Ticket = default; CoverActor = 0; _completed = -1f; _offered = false; Reason = reason;
        }
        public void Clear(string reason) { Cancel(reason); _state = _baseline = default; }
    }
}
