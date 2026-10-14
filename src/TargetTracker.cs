using System;
using EFT;

namespace ImmersiveVoice
{
    // A small cache of locally seen hostiles, not a roster or kill-feed listener.
    // At most one tracking ray per vision tick, regardless of how many are known.
    internal sealed class TargetTracker
    {
        public const int Capacity = 4;
        private sealed class Slot
        {
            public VisibleContact Contact;
            public float SeenAt, TraceAt;
            public int Version;
            public string Reason;
        }
        private readonly Slot[] _slots = { new Slot(), new Slot(), new Slot(), new Slot() };
        private readonly ViewProbe _probe;
        private readonly Action<string> _record;
        private int _next;
        public TargetTracker(ViewProbe probe, Action<string> record) { _probe = probe; _record = record; }
        public void Remember(VisibleContact seen, float now, bool tracing)
        {
            int selected = -1;
            float oldest = float.MaxValue;
            for (int i = 0; i < Capacity; i++)
            {
                Slot slot = _slots[i];
                if (slot.Contact?.Id == seen.Id)
                {
                    slot.Contact = seen; slot.SeenAt = now;
                    if (!seen.Alive) { Report(i, now, "VisibleDeath", tracing, force: true); slot.Contact = null; }
                    return;
                }
                float age = slot.Contact == null ? -1f : slot.SeenAt;
                if (age < oldest) { oldest = age; selected = i; }
            }
            if (!seen.Alive || seen.Ally || selected < 0) return;
            Slot picked = _slots[selected];
            picked.Version++; picked.Contact = seen; picked.SeenAt = now; picked.Reason = null; picked.TraceAt = 0f;
            Report(selected, now, "RememberedHostile", tracing, force: true);
        }
        public VisibleContact Track(float now, Player local, float range, FikaBridge fika, VisibleContact gaze, bool tracing)
        {
            for (int n = 0; n < Capacity; n++)
            {
                int index = _next; _next = (_next + 1) % Capacity;
                Slot slot = _slots[index];
                if (slot.Contact == null) continue;
                if (now - slot.SeenAt > ContactMemory.DeathWitnessWindow)
                { Report(index, now, "ObservationExpired", tracing, force: true); slot.Contact = null; continue; }
                if (gaze?.Id == slot.Contact.Id) continue;
                VisibleContact seen = _probe.Track(local, slot.Contact, range, fika);
                Report(index, now, _probe.LastReason, tracing);
                return seen; // Includes a failed ray: never spend another ray to find a success.
            }
            return null;
        }
        private void Report(int index, float now, string reason, bool tracing, bool force = false)
        {
            Slot slot = _slots[index];
            if (!tracing || reason == slot.Reason || (!force && now < slot.TraceAt)) return;
            slot.Reason = reason; slot.TraceAt = now + 1f;
            _record(FormattableString.Invariant($"Event=VisualTrack TargetSlot={index} TargetVersion={slot.Version} ObservationAge={now - slot.SeenAt:F2} Reason={reason}"));
        }
        public void Clear() { foreach (Slot slot in _slots) slot.Contact = null; _next = 0; }
    }
}
