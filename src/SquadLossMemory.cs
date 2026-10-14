namespace ImmersiveVoice
{
    internal enum SquadLossEvidence { NotCorpse, NoLivingWitness, ConfirmingCorpse, AlreadyConsidered }
    internal struct SquadLoss
    {
        public int Slot, Version;
        public bool Exists;
    }
    // Identity is supplied by the three-slot roster. Health=false is never
    // evidence: Fika also sets it during a revivable downed state.
    internal sealed class SquadLossMemory
    {
        private sealed class Peer
        {
            public int Version;
            public bool MetAlive, Decided;
            public float Since = -1f, SeenAt = -1000f;
        }
        private readonly Peer[] _peers = { new Peer(), new Peer(), new Peer() };
        private float _next;
        public void Bind(int slot) { Peer p = _peers[slot]; p.Version++; p.MetAlive = p.Decided = false; p.Since = -1f; p.SeenAt = -1000f; }
        public void Alive(int slot)
        {
            Peer p = _peers[slot];
            if (p.Since >= 0f) { p.Version++; p.Since = -1f; p.SeenAt = -1000f; }
            p.MetAlive = true;
        }
        public SquadLossEvidence Observe(int slot, float now, bool actualCorpse)
        {
            Peer p = _peers[slot];
            if (!actualCorpse) return SquadLossEvidence.NotCorpse;
            if (!p.MetAlive) return SquadLossEvidence.NoLivingWitness;
            if (now - p.SeenAt > SquadMemory.Freshness) p.Since = now;
            p.SeenAt = now;
            return p.Decided ? SquadLossEvidence.AlreadyConsidered : SquadLossEvidence.ConfirmingCorpse;
        }
        public SquadLoss Decide(float now)
        {
            if (now < _next) return default;
            for (int i = 0; i < _peers.Length; i++)
            {
                Peer p = _peers[i];
                if (p.Decided || p.Since < 0f || p.SeenAt - p.Since < 0.35f || now - p.SeenAt > SquadMemory.Freshness) continue;
                p.Decided = true; _next = now + 45f;
                return new SquadLoss { Slot = i, Version = p.Version, Exists = true };
            }
            return default;
        }
        public bool Valid(SquadLoss loss, float now)
            => loss.Exists && loss.Slot >= 0 && loss.Slot < _peers.Length
                && _peers[loss.Slot].Version == loss.Version && _peers[loss.Slot].Since >= 0f
                && now - _peers[loss.Slot].SeenAt <= 6f;
        public void Pause()
        {
            for (int i = 0; i < _peers.Length; i++)
            { _peers[i].Since = -1f; _peers[i].SeenAt = -1000f; _peers[i].Version++; }
        }
    }
}
