using System;
using System.Collections.Generic;

namespace ImmersiveVoice
{
    internal enum ReactionKind { Reload, Ammo, Malfunction, Grenade, Injury, Heal, Contact, LostVisual, EnemyDown, Recovery, Reply, RepeatedContact, MedicalRequest, AfterTreatment, Squad, FriendlyDown, NpcReply }

    internal sealed class Reaction
    {
        public ReactionKind Kind;
        public VoiceCue Cue;
        public int Priority;
        public float ReadyAt, ExpiresAt;
        public string Subject;
        public bool RequiresEmpty;
        public bool FriendlyDamage;
        public int DamageVersion;
        public int ExchangeVersion, MalfunctionEpisode;
        public int TreatmentVersion;
        public int NpcEpisode;
        public SquadDecision Squad;
        public SquadLoss Loss;
        public string LastWaitReason;
        public string InvalidReason;
    }

    internal sealed class ReactionQueue
    {
        public static bool ExchangeInvalidates(ReactionKind kind, VoiceIntent intent, bool changed)
            => changed && (kind == ReactionKind.EnemyDown && intent != VoiceIntent.TacticalEnemyDown
                || kind == ReactionKind.Recovery || kind == ReactionKind.AfterTreatment);

        private readonly List<Reaction> _pending = new List<Reaction>();
        private readonly Action<Reaction, string> _dropped;
        public ReactionQueue(Action<Reaction, string> dropped = null) { _dropped = dropped; }
        public int Count => _pending.Count;
        public bool ContainsGroup(string group) => _pending.Exists(x => x.Cue.Group == group);

        // Distinct witnessed deaths update one pending ending, without extending
        // its original expiry. This is not used for repeated engine hints.
        public void OfferEnding(Reaction reaction, float now)
        {
            Expire(now);
            foreach (var old in _pending)
                if (old.Kind == ReactionKind.EnemyDown && old.ExchangeVersion == reaction.ExchangeVersion)
                    reaction.ExpiresAt = Math.Min(reaction.ExpiresAt, old.ExpiresAt);
            RemoveGroup("kill", "NewObservedDown");
            Offer(reaction, now);
        }

        public void Offer(Reaction reaction, float now)
        {
            Expire(now);
            for (int i = 0; i < _pending.Count; i++)
            {
                Reaction old = _pending[i];
                if (old.Cue.Group != reaction.Cue.Group) continue;
                // Repeated engine callbacks may not extend the lifetime or delay
                // a queued line. A more severe event may replace a mild one.
                if (old.Priority >= reaction.Priority) { _dropped?.Invoke(reaction, "DuplicateCategory"); return; }
                _pending.RemoveAt(i);
                _dropped?.Invoke(old, "ReplacedByPriority");
                break;
            }
            if (_pending.Count >= 12)
            {
                int lowest = 0;
                for (int i = 1; i < _pending.Count; i++)
                    if (_pending[i].Priority < _pending[lowest].Priority) lowest = i;
                if (_pending[lowest].Priority >= reaction.Priority) { _dropped?.Invoke(reaction, "QueueFull"); return; }
                _dropped?.Invoke(_pending[lowest], "ReplacedByPriority");
                _pending.RemoveAt(lowest);
            }
            _pending.Add(reaction);
        }

        public Reaction Next(float now, Func<Reaction, bool> valid, Func<Reaction, bool> allowed)
        {
            Expire(now);
            Reaction best = null;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Reaction item = _pending[i];
                if (!valid(item)) { _pending.RemoveAt(i); _dropped?.Invoke(item, "ContextInvalid"); continue; }
                if (item.ReadyAt > now || !allowed(item)) continue;
                if (best == null || item.Priority > best.Priority
                    || (item.Priority == best.Priority && item.ReadyAt < best.ReadyAt)) best = item;
            }
            return best;
        }

        public bool Reserve(float now)
        {
            Expire(now);
            // Reserve a short reaction window, not the entire remaining TTL.
            return _pending.Exists(x => x.ReadyAt <= now + 0.7f);
        }
        public void Remove(Reaction item) => _pending.Remove(item);
        public void RemoveGroup(string group, string reason = "ActionEnded") => DropWhere(x => x.Cue.Group == group, reason);
        public void RemoveSubject(string group, string subject, string reason)
            => DropWhere(x => x.Cue.Group == group && x.Subject == subject, reason);
        public void RemoveKind(ReactionKind kind, string reason) => DropWhere(x => x.Kind == kind, reason);
        public void Clear() => DropWhere(x => true, "Discarded");
        private void Expire(float now) => DropWhere(x => x.ExpiresAt <= now, "Expired");
        private void DropWhere(Predicate<Reaction> predicate, string reason)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Reaction r = _pending[i];
                if (!predicate(r)) continue;
                _pending.RemoveAt(i);
                _dropped?.Invoke(r, reason);
            }
        }
    }

    // Fixed window from the FIRST impact: pellets/fragments cannot postpone it.
    // Damage is callback-reported damage, not asserted to be actual HP loss.
    internal sealed class DamageWindow
    {
        public const float Delay = 0.8f; // Preserve the existing injury response delay.
        public int Hits { get; private set; }
        public float Damage { get; private set; }
        public float StartedAt { get; private set; }
        public InjuryLevel Severity { get; private set; }
        public bool FriendlyOnly { get; private set; }
        public bool Ready(float now) => Hits > 0 && now >= StartedAt + Delay;

        public void Add(float now, float damage, InjuryLevel severity, bool friendly)
        {
            if (Hits == 0) { StartedAt = now; FriendlyOnly = true; }
            Hits++;
            Damage += Math.Max(0f, damage);
            if (severity > Severity) Severity = severity;
            FriendlyOnly &= friendly;
        }
        public void Clear() { Hits = 0; Damage = 0f; Severity = InjuryLevel.Healthy; FriendlyOnly = false; }
    }

    // Stores only identifiers of actual attackers, targets the player fired at,
    // and ray-confirmed observations. No world-wide player collection is used.
    internal sealed class ContactMemory
    {
        public const float ContactGrace = 4f, DeathWitnessWindow = 10f;
        private sealed class Entry
        {
            public float HostileUntil, SeenAliveAt = -1000, FirstSeenAt, LastSeenAt = -1000;
            public bool Contact, Lost, Dead;
        }
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private string _lastEnemy;

        public void Hostile(string id, float now)
        {
            if (string.IsNullOrEmpty(id)) return;
            Get(id, now).HostileUntil = now + 60f;
        }

        public bool IsHostile(string id, float now) => id != null && _entries.TryGetValue(id, out var e) && e.HostileUntil > now;

        public string ContactBlockReason(string id, float now)
        {
            if (id == null || !_entries.TryGetValue(id, out var e)) return "ContactForgotten";
            if (e.Dead) return "TargetAlreadyDown";
            if (e.HostileUntil <= now) return "HostilityExpired";
            return now - e.SeenAliveAt > ContactGrace ? "ContactObservationExpired" : null;
        }

        public string DeathBlockReason(string id, float now)
        {
            if (id == null || !_entries.TryGetValue(id, out var e) || e.HostileUntil <= now) return "NoKnownHostility";
            if (e.Dead) return "DeathAlreadyConfirmed";
            if (e.SeenAliveAt < 0f) return "NoLivingWitness";
            return now - e.SeenAliveAt > DeathWitnessWindow ? "LivingWitnessExpired" : null;
        }

        public ReactionKind? Observe(string id, bool alive, bool ally, float now, bool focused = true)
        {
            if (string.IsNullOrEmpty(id) || ally) return null;
            Entry e = Get(id, now);
            bool hostile = e.HostileUntil > now;
            if (alive)
            {
                if (now - e.LastSeenAt > 1.05f) e.FirstSeenAt = now;
                e.SeenAliveAt = e.LastSeenAt = now;
                if (!hostile) return null;
                if (focused || _lastEnemy == null) _lastEnemy = id;
                if (!e.Contact && now - e.FirstSeenAt >= 0.2f)
                {
                    e.Contact = true;
                    return ReactionKind.Contact;
                }
                if (e.Lost && now - e.FirstSeenAt >= 0.2f)
                {
                    e.Lost = false;
                    return ReactionKind.RepeatedContact;
                }
            }
            else if (DeathBlockReason(id, now) == null)
            {
                e.Dead = true;
                e.LastSeenAt = now;
                return ReactionKind.EnemyDown;
            }
            return null;
        }

        public string Lost(float now, bool combat)
        {
            if (!combat || _lastEnemy == null || !_entries.TryGetValue(_lastEnemy, out var e)
                || !e.Contact || e.Dead || e.Lost || e.HostileUntil <= now) return null;
            if (now - e.LastSeenAt < 4f || now - e.LastSeenAt > 15f) return null;
            e.Lost = true;
            return _lastEnemy;
        }

        public bool LostStillValid(string id, float now) => id != null && _entries.TryGetValue(id, out var e)
            && e.Lost && !e.Dead && e.HostileUntil > now && now - e.LastSeenAt >= 4f;
        public bool DeathConfirmed(string id) => id != null && _entries.TryGetValue(id, out var e) && e.Dead;
        public bool RecentLivingThreat(float now, float window)
        {
            foreach (var e in _entries.Values)
                if (!e.Dead && e.HostileUntil > now && now - e.SeenAliveAt <= window) return true;
            return false;
        }
        public void Clear() { _entries.Clear(); _lastEnemy = null; }

        private Entry Get(string id, float now)
        {
            if (_entries.TryGetValue(id, out var existing)) return existing;
            if (_entries.Count >= 32)
            {
                string oldest = null;
                float time = float.MaxValue;
                foreach (var pair in _entries)
                    if (pair.Value.LastSeenAt < time) { time = pair.Value.LastSeenAt; oldest = pair.Key; }
                if (oldest != null) _entries.Remove(oldest);
            }
            var entry = new Entry { FirstSeenAt = now };
            _entries.Add(id, entry);
            return entry;
        }
    }

    // Native Awareness can outlive a fight. Only recent exchanges renew this
    // short window, and a witnessed death closes it unless another threat was seen.
    internal sealed class CombatMemory
    {
        private float _exchange = -1000f, _incoming = -1000f, _defeat = -1000f;
        private float _start = -1000f, _lastEvidence = -1000f, _pressure, _pressureAt, _peak;
        private float _shotCountedAt = -1000f, _hitCountedAt = -1000f;
        private bool _tookHit, _hadMalfunction;
        private readonly Queue<float> _observedDowns = new Queue<float>();
        public int ExchangeVersion { get; private set; }
        public bool EndingSelected { get; private set; }
        public bool TookHostileHit(float now) => now - _lastEvidence < 15f && _tookHit;
        public bool HadMalfunction(float now) => now - _lastEvidence < 15f && _hadMalfunction;
        public int RecentObservedDowns(float now)
        {
            while (_observedDowns.Count > 0 && now - _observedDowns.Peek() >= 12f) _observedDowns.Dequeue();
            return _observedDowns.Count;
        }
        public void SelectEnding() { EndingSelected = true; }
        public void Malfunction(float now)
        {
            // A faulty weapon never starts or prolongs a fight by itself.
            if (Settling(now) && now - _lastEvidence < 15f) _hadMalfunction = true;
        }
        public float Duration(float now) => now - _lastEvidence < 15f ? Math.Max(0f, _lastEvidence - _start) : 0f;
        public float PeakPressure(float now) => now - _lastEvidence < 15f ? _peak : 0f;
        public void Shot(float now, bool visibleTarget, bool recentThreat)
        {
            if (!visibleTarget && !recentThreat && !(_incoming > _defeat && now - _incoming < 8f)) return;
            Record(now, now - _shotCountedAt >= 1f ? 1f : 0f);
            if (now - _shotCountedAt >= 1f) _shotCountedAt = now;
            _exchange = now;
        }
        public void Incoming(float now)
        {
            Record(now, now - _hitCountedAt >= DamageWindow.Delay ? 2f : 0f);
            _tookHit = true;
            if (now - _hitCountedAt >= DamageWindow.Delay) _hitCountedAt = now;
            _incoming = _exchange = now;
        }
        private void Record(float now, float weight)
        {
            if (!Active(now)) { ExchangeVersion++; EndingSelected = false; }
            if (now - _lastEvidence >= 15f)
            {
                _start = now; _pressure = _peak = 0f;
                _tookHit = _hadMalfunction = false;
                _observedDowns.Clear();
            }
            _pressure = Math.Min(10f, Math.Max(0f, _pressure - Math.Max(0f, now - _pressureAt) * 0.5f) + weight);
            _peak = Math.Max(_peak, _pressure);
            _pressureAt = _lastEvidence = now;
        }
        public void Defeat(float now, bool anotherThreat)
        {
            RecentObservedDowns(now);
            if (_observedDowns.Count >= 8) _observedDowns.Dequeue();
            _observedDowns.Enqueue(now);
            _defeat = now;
            if (!anotherThreat) _exchange = -1000f;
        }
        public bool Active(float now) => now - _exchange < 8f;
        public bool Settling(float now) => now - Math.Max(_exchange, _defeat) < 15f;
        public void Clear()
        {
            _exchange = _incoming = _defeat = _start = _lastEvidence = _shotCountedAt = _hitCountedAt = -1000f;
            _pressure = _peak = _pressureAt = 0f;
            _tookHit = _hadMalfunction = EndingSelected = false;
            _observedDowns.Clear();
            ExchangeVersion++;
        }
    }

    // Native hints decide once per unresolved weapon fault, even after switching
    // hands or pausing voice. Polling only validates state; it never requests speech.
    internal sealed class MalfunctionMemory
    {
        private sealed class Episode { public int Id; public bool Decided; }
        private readonly Dictionary<string, Episode> _active = new Dictionary<string, Episode>();
        private int _nextId;
        public int Count => _active.Count;
        public void Observe(string weapon, bool faulty)
        {
            if (string.IsNullOrEmpty(weapon)) return;
            if (!faulty) { _active.Remove(weapon); return; }
            // Do not evict unresolved faults and accidentally announce them again.
            if (!_active.ContainsKey(weapon) && _active.Count < 16)
                _active.Add(weapon, new Episode { Id = ++_nextId });
        }
        public int Begin(string weapon, bool faulty)
        {
            Observe(weapon, faulty);
            if (weapon == null || !_active.TryGetValue(weapon, out var episode) || episode.Decided) return 0;
            episode.Decided = true;
            return episode.Id;
        }
        public bool Matches(string weapon, int episode)
            => weapon != null && episode != 0 && _active.TryGetValue(weapon, out var current) && current.Id == episode;
        public void Clear() => _active.Clear();
    }

    // Native hints often precede the operation edge; both belong to one reload.
    internal sealed class ReloadDecision
    {
        private bool _operation, _decided;
        private float _nativeUntil;
        public void Native(float now)
        {
            if (!_operation && now >= _nativeUntil) _decided = false;
            _nativeUntil = now + 1f;
        }
        public void Update(float now, bool operation)
        {
            if (operation && !_operation && now >= _nativeUntil) _decided = false;
            if (!operation && _operation) { _decided = false; _nativeUntil = 0f; }
            _operation = operation;
        }
        public bool Begin()
        {
            if (_decided) return false;
            _decided = true;
            return true;
        }
        public bool Valid(float now) => _operation || now < _nativeUntil;
        public void Spoken() { _decided = true; }
        public void Clear() { _operation = _decided = false; _nativeUntil = 0f; }
    }
}
