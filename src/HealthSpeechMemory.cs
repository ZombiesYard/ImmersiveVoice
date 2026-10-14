namespace ImmersiveVoice
{
    internal enum HealthCue { None, Injury, SevereInjury, CriticalInjury, Bleeding, ArmFracture, LegFracture }
    internal enum HealthDisposition { Ready, Expressed, MissingAudio, Treating, RetryLimit }

    // Shared by passive native Y hints and the event director. This only remembers
    // local wound facts; damage reactions and verified treatment outcomes remain events.
    internal sealed class HealthSpeechMemory
    {
        public const float ImprovementDelay = 3f, RecoveryDelay = 5f, TreatmentQuiet = 3f;
        private int _state, _improvingState;
        private float _improvingAt = -1f, _quietUntil;
        private bool _known, _usingMedicine;
        public int Version { get; private set; }
        public HealthDisposition Disposition { get; private set; }
        public bool Considered => Disposition != HealthDisposition.Ready;
        private int _failures;
        private int? _selectedPhrase;
        public bool MatchesSelection(int phrase) => !_selectedPhrase.HasValue || _selectedPhrase == phrase;
        public void Select(int phrase) { _selectedPhrase = phrase; }

        private static int State(BarkContext c) => (int)c.Injury | (c.Bleeding ? 4 : 0)
            | (c.HeavyBleeding ? 8 : 0) | (c.ArmFracture ? 16 : 0) | (c.LegFracture ? 32 : 0);
        private static bool Worsened(int current, int previous)
            => (current & 3) > (previous & 3) || (current & ~previous & ~3) != 0;

        public void Observe(float now, BarkContext context)
        {
            int current = State(context);
            if (!_known || Worsened(current, _state))
            {
                _known = true; _state = current; _improvingAt = -1f;
                BeginEvent(); Version++;
            }
            else if (current == _state) _improvingAt = -1f;
            else
            {
                if (_improvingAt < 0f || _improvingState != current)
                { _improvingAt = now; _improvingState = current; }
                if (now - _improvingAt >= (current == 0 ? RecoveryDelay : ImprovementDelay))
                {
                    _state = current; _improvingAt = -1f; Version++;
                    // Partial recovery alone does not reopen passive complaints.
                    if (current == 0) BeginEvent();
                }
            }
            if (context.Healing)
            {
                if (!_usingMedicine) Disposition = HealthDisposition.Treating;
                _quietUntil = now + TreatmentQuiet;
            }
            else if (_usingMedicine) _quietUntil = now + TreatmentQuiet;
            _usingMedicine = context.Healing;
        }

        public string BlockReason(HealthCue cue, BarkContext context, float now, bool healthPending = false, bool treatmentPending = false)
        {
            if (cue == HealthCue.None) return null;
            if (context.Healing) return "HealingInProgress";
            if (now < _quietUntil || treatmentPending) return "TreatmentSettling";
            if (context.RecentlyHit || healthPending) return "HealthEventPreferred";
            bool matches = cue == HealthCue.Injury ? context.Injury == InjuryLevel.Injured
                : cue == HealthCue.SevereInjury ? context.Injury == InjuryLevel.BadlyInjured
                : cue == HealthCue.CriticalInjury ? context.Injury == InjuryLevel.Dying
                : cue == HealthCue.Bleeding ? context.Bleeding || context.HeavyBleeding
                : cue == HealthCue.ArmFracture ? context.ArmFracture
                : cue == HealthCue.LegFracture && context.LegFracture;
            if (!matches) return "HealthHintNoLongerMatches";
            return Disposition == HealthDisposition.RetryLimit ? "HealthPlaybackRetryLimit"
                : Considered ? "SameHealthEpisode" : null;
        }

        // Selection is not playback. Retry a temporary rejection, but never cycle
        // through alternative meanings or retry indefinitely while wounded.
        public void BeginEvent() { Disposition = HealthDisposition.Ready; _failures = 0; _selectedPhrase = null; }
        public void Consider() { Disposition = HealthDisposition.Expressed; }
        public void Missing() { Disposition = HealthDisposition.MissingAudio; }
        public void Failed()
        {
            if (Disposition == HealthDisposition.Ready && ++_failures >= 3) Disposition = HealthDisposition.RetryLimit;
        }
        public void Pause() { _improvingAt = -1f; }
    }
}
