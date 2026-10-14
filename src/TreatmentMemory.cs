namespace ImmersiveVoice
{
    internal struct TreatmentSnapshot
    {
        public float Health;
        public InjuryLevel Injury;
        // Body-part masks preserve partial progress when another limb still has
        // the same condition. These are only the local player's active effects.
        public int Bleeds, HeavyBleeds, Fractures;
        public bool Valid => !float.IsNaN(Health) && !float.IsInfinity(Health) && Health >= 0f;
        public bool Concern => Injury >= InjuryLevel.BadlyInjured || (Bleeds | HeavyBleeds | Fractures) != 0;
        public bool ImprovedFrom(TreatmentSnapshot before)
            => Valid && before.Valid && (Health >= before.Health + 0.5f || Injury < before.Injury
                || (before.Bleeds & ~Bleeds) != 0 || (before.HeavyBleeds & ~HeavyBleeds) != 0
                || (before.Fractures & ~Fractures) != 0);
        public bool NewConcernSince(TreatmentSnapshot before)
            => Injury > before.Injury || (Bleeds & ~before.Bleeds) != 0
                || (HeavyBleeds & ~before.HeavyBleeds) != 0 || (Fractures & ~before.Fractures) != 0;
    }

    internal enum TreatmentOutcome { None, RemainingConcern, NoRemainingConcern, NoObservedImprovement, ContextChanged, Expired }

    // Observed improvement during medicine use is not a claim that the animation
    // completed, or that this item caused every health change (e.g. an active stim).
    internal sealed class TreatmentMemory
    {
        public const float QuietDelay = 3f, MaximumWindow = 90f;
        private float _startedAt, _stoppedAt = -1f;
        private int _exchange, _damage, _decidedExchange = -1;
        public bool Tracking { get; private set; }
        public int Version { get; private set; }
        public TreatmentSnapshot Before { get; private set; }
        public TreatmentSnapshot After { get; private set; }

        public bool Begin(float now, BarkContext context, TreatmentSnapshot snapshot, int exchange)
        {
            Version++;
            if (Tracking && exchange == _exchange && context.DamageVersion == _damage
                && now - _startedAt < MaximumWindow && (_stoppedAt < 0f || now - _stoppedAt < QuietDelay))
            {
                _stoppedAt = -1f;
                return true;
            }
            Stop();
            if (!snapshot.Valid || !context.Wounded || context.Combat == CombatPhase.Calm || exchange == _decidedExchange)
                return false;
            Before = snapshot;
            _startedAt = now; _exchange = exchange; _damage = context.DamageVersion;
            Tracking = true;
            return true;
        }

        public TreatmentOutcome Observe(float now, bool usingMedicine, BarkContext context,
            TreatmentSnapshot snapshot, int exchange)
        {
            if (!Tracking) return TreatmentOutcome.None;
            After = snapshot;
            if (now - _startedAt >= MaximumWindow) { Stop(); return TreatmentOutcome.Expired; }
            if (!snapshot.Valid || exchange != _exchange || context.DamageVersion != _damage || snapshot.NewConcernSince(Before))
            { Stop(); return TreatmentOutcome.ContextChanged; }
            if (usingMedicine) { _stoppedAt = -1f; return TreatmentOutcome.None; }
            if (context.Healing || context.RecentlyHit || context.Combat == CombatPhase.Engaged)
            { Stop(); return TreatmentOutcome.ContextChanged; }
            if (_stoppedAt < 0f) _stoppedAt = now;
            if (now - _stoppedAt < QuietDelay) return TreatmentOutcome.None;
            Stop();
            if (!snapshot.ImprovedFrom(Before)) return TreatmentOutcome.NoObservedImprovement;
            // At most one improved ending per exchange, even if its exact clip is
            // missing or the queued line loses to an urgent reaction.
            _decidedExchange = exchange;
            return snapshot.Concern ? TreatmentOutcome.RemainingConcern : TreatmentOutcome.NoRemainingConcern;
        }

        public void Cancel() { Version++; Stop(); }
        public void Clear() { Cancel(); _decidedExchange = -1; }
        private void Stop() { Tracking = false; _stoppedAt = -1f; }
    }
}
