namespace ImmersiveVoice
{
    // One instance per known human. An unobstructed, nearby companion remains
    // perceptible behind the camera after being seen/heard; never through walls.
    internal sealed class SquadAwareness
    {
        public const float CompanionRange = 12f, FamiliarityGrace = 8f;
        private float _knownUntil = -1f;
        public const float ReportGrace = 2.5f, ReportRange = 14f;
        private float _reportAt = -1000f;
        private SquadPoint _reportSelf, _reportOther;
        public void Sight(float now, bool clear, bool facing, float distance, float height)
        {
            if (!clear) return;
            if (facing || Nearby(now, true, distance, height)) _knownUntil = now + FamiliarityGrace;
        }
        public void Heard(float now) { _knownUntil = now + FamiliarityGrace; }
        public bool Nearby(float now, bool freshClear, float distance, float height)
            => freshClear && now <= _knownUntil && distance >= 0f && distance <= CompanionRange
                && System.Math.Abs(height) < 2f;
        // A report may address a companion just observed at a doorway. This is
        // not perception: it supplies no movement, targeting or hit evidence.
        public void ObserveReport(float now, SquadPoint self, SquadPoint other)
        {
            SquadPoint separation = other - self;
            float range = ReportNearby(now, self) ? ReportRange : CompanionRange;
            if (!self.Finite || !other.Finite || separation.LengthSquared > range * range || System.Math.Abs(separation.Y) >= 2f)
            { _reportAt = -1000f; return; }
            _reportAt = now; _reportSelf = self; _reportOther = other;
        }
        public bool ReportNearby(float now, SquadPoint self)
        {
            SquadPoint separation = _reportOther - self;
            return now >= _reportAt && now - _reportAt <= ReportGrace && self.Finite
                && (self - _reportSelf).LengthSquared <= 16f
                && separation.LengthSquared <= ReportRange * ReportRange && System.Math.Abs(separation.Y) < 2f;
        }
        public void Clear() { _knownUntil = -1f; _reportAt = -1000f; }
    }
}
