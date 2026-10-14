namespace ImmersiveVoice
{
    internal enum MedicalSupplies { Unknown, Available, Missing }

    internal static class MedicalRequestPolicy
    {
        public const int Priority = 80;
        public const float Repeat = 60f, StableDelay = 3f, RecoveryReset = 5f;
        public static bool NeedsCare(BarkContext context)
            => context.Injury >= InjuryLevel.BadlyInjured || context.HeavyBleeding;
        public static bool Eligible(BarkContext context)
            => NeedsCare(context) && context.TeamInformation && !context.Healing && !context.RecentlyHit;
        public static bool Recipient(bool present, bool human, bool alive, float seenAge)
            => present && human && alive && seenAge >= 0f && seenAge <= 1f;

        // A medicine that is present but not currently applicable is uncertainty,
        // not proof of shortage (painkillers, stimulants and custom meds included).
        public static MedicalSupplies Medicine(bool hasResource, float remaining, bool applicable)
        {
            if (!hasResource || float.IsNaN(remaining) || float.IsInfinity(remaining) || remaining < 0f)
                return MedicalSupplies.Unknown;
            if (remaining == 0f) return applicable ? MedicalSupplies.Unknown : MedicalSupplies.Missing;
            return applicable ? MedicalSupplies.Available : MedicalSupplies.Unknown;
        }
    }

    // One decision for a continuous serious condition. Losing sight, missing
    // clips, manual silence or temporary treatment never reroll that decision.
    internal sealed class MedicalRequestDecision
    {
        private bool _decided;
        private float _stableSince = -1f, _recoveredSince = -1f;
        public bool Decided => _decided;
        public bool Observe(float now, BarkContext context, MedicalSupplies supplies)
        {
            if (!MedicalRequestPolicy.NeedsCare(context))
            {
                _stableSince = -1f;
                if (_recoveredSince < 0f) _recoveredSince = now;
                if (now - _recoveredSince >= MedicalRequestPolicy.RecoveryReset) _decided = false;
                return false;
            }
            _recoveredSince = -1f;
            if (!MedicalRequestPolicy.Eligible(context) || supplies != MedicalSupplies.Missing)
            { _stableSince = -1f; return false; }
            if (_decided) return false;
            if (_stableSince < 0f) _stableSince = now;
            if (now - _stableSince < MedicalRequestPolicy.StableDelay) return false;
            Spoken();
            return true;
        }
        public void Spoken() { _decided = true; _stableSince = -1f; }
        public void Pause() { _stableSince = _recoveredSince = -1f; }
    }
}
