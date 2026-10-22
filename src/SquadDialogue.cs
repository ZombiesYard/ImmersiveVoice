using System;

namespace ImmersiveVoice
{
    internal struct DialogueTicket
    {
        public int Caller, Epoch, Sequence;
        public bool Exists => Caller > 0 && Epoch > 0 && Sequence > 0;
        public bool Same(DialogueTicket other) => Caller == other.Caller && Epoch == other.Epoch && Sequence == other.Sequence;
    }

    // Four callers, one current call each. No reassignment after a grant, even if
    // the winner cancels: a delayed previous winner must never race a new one.
    internal sealed class DialogueArbiter
    {
        private sealed class Call
        {
            public DialogueTicket Ticket;
            public int Phrase, Clip, Winner, ReplyClip;
            public float RepliedAt;
            public bool Replied;
            public float Opened, Until;
            public object Owner, Identity;
        }
        private readonly Call[] _calls = { new Call(), new Call(), new Call(), new Call() };
        public bool Open(DialogueTicket ticket, int phrase, int clip, float duration, float now, object owner = null, object identity = null)
        {
            if (!ticket.Exists || phrase <= 0 || clip < 0 || float.IsNaN(duration) || duration < 0 || duration > 20) return false;
            Call found = null, empty = null;
            foreach (Call c in _calls)
            {
                if (c.Ticket.Caller == ticket.Caller) found = c;
                if (!c.Ticket.Exists && empty == null) empty = c;
            }
            Call target = found ?? empty;
            if (target == null) return false;
            if (found != null && (found.Ticket.Epoch != ticket.Epoch || ticket.Sequence <= found.Ticket.Sequence
                || !ReferenceEquals(owner, found.Owner))) return false;
            target.Ticket = ticket; target.Phrase = phrase; target.Clip = clip;
            target.Opened = now; target.Until = now + duration + 7f; target.Owner = owner; target.Identity = identity; target.Winner = 0; target.Replied = false;
            return true;
        }
        public DialogueTicket Match(int caller, int phrase, int clip, float heardAt, float now)
        {
            foreach (Call c in _calls)
                if (c.Ticket.Caller == caller && c.Phrase == phrase && c.Clip == clip && now < c.Until
                    && Math.Abs(c.Opened - heardAt) <= 2f) return c.Ticket;
            return default;
        }
        public int Winner(DialogueTicket ticket, float now)
        {
            foreach (Call c in _calls)
                if (c.Ticket.Same(ticket) && now < c.Until) return c.Winner;
            return -1;
        }
        public bool Claim(DialogueTicket ticket, int actor, float now)
        {
            if (actor <= 0 || actor == ticket.Caller) return false;
            foreach (Call c in _calls)
                if (c.Ticket.Same(ticket) && now < c.Until && c.Winner == 0)
                { c.Winner = actor; return true; }
            return false;
        }
        // A grant is not proof of playback. Record only the winner's actual
        // Covering callback; match it independently to native audio at the caller.
        public bool Reply(DialogueTicket ticket, int actor, int phrase, int clip, float duration, float now)
        {
            if (phrase != 60 || clip < 0 || float.IsNaN(duration) || duration <= 0 || duration > 20) return false;
            foreach (Call c in _calls)
                if (c.Ticket.Same(ticket) && now < c.Until && SquadDialogue.ReadyCall(c.Phrase) && c.Winner == actor && actor > 0 && !c.Replied)
                { c.Replied = true; c.ReplyClip = clip; c.RepliedAt = now; return true; }
            return false;
        }
        public DialogueTicket MatchReply(int caller, int actor, int clip, float heardAt, float now)
        {
            foreach (Call c in _calls)
                if (c.Ticket.Caller == caller && c.Replied && c.Winner == actor && c.ReplyClip == clip
                    && now < c.RepliedAt + 2.5f && Math.Abs(c.RepliedAt - heardAt) <= 2f) return c.Ticket;
            return default;
        }
        public void RemoveOwner(object owner)
        {
            foreach (Call c in _calls)
                if (ReferenceEquals(c.Owner, owner)) c.Ticket = default;
        }
        // On a new call only: four dictionary lookups release departed or replaced
        // actors, including a reconnect reusing the same network ID on a client.
        public void Reconcile(Func<int, object> identity)
        {
            foreach (Call c in _calls)
                if (c.Ticket.Exists && !ReferenceEquals(c.Identity, identity(c.Ticket.Caller)))
                { c.Ticket = default; c.Owner = c.Identity = null; }
        }
        public void Clear() { foreach (Call c in _calls) { c.Ticket = default; c.Owner = c.Identity = null; } }
    }

    internal static class SquadDialogue
    {
        public static VoiceIntent Intent(SquadDecision d)
        {
            if (d.Action == SquadAction.Compliment) return VoiceIntent.TeamGoodWork;
            if (d.Reply)
                return d.Action == SquadAction.Covering ? VoiceIntent.TeamCovering
                    : d.Action == SquadAction.Holding || d.Action == SquadAction.Regrouping ? VoiceIntent.TeamOnPosition
                    : VoiceIntent.TeamFollowing;
            return d.Action == SquadAction.PushNow ? VoiceIntent.TeamPush
                : d.Action == SquadAction.Suppressing ? VoiceIntent.TeamSuppress
                : d.Action == SquadAction.SpreadOut ? VoiceIntent.TeamSpreadOut
                : d.Action == SquadAction.GetInCover ? VoiceIntent.TeamGetInCover
                : d.Action == SquadAction.Following ? VoiceIntent.TeamFollowing
                : d.Action == SquadAction.Covering ? VoiceIntent.TeamCovering
                : d.Action == SquadAction.Advancing ? VoiceIntent.TeamAdvance
                : d.Action == SquadAction.Leading ? VoiceIntent.TeamLead
                : d.Action == SquadAction.RequestCover ? VoiceIntent.TeamNeedCover
                : d.Action == SquadAction.Rallying ? VoiceIntent.TeamRegroup
                : d.Action == SquadAction.HoldTeam ? VoiceIntent.TeamHold : VoiceIntent.TeamRetreat;
        }
        // Native EPhraseTrigger IDs. Test against the installed enum and bank.
        // This is a semantic mapping, never a list of fallback audio choices.
        public static int Phrase(SquadDecision d)
        {
            if (d.Action == SquadAction.Compliment) return 66; // GoodWork
            if (d.Reply) return d.Action == SquadAction.Covering ? 60
                : d.Action == SquadAction.Holding || d.Action == SquadAction.Regrouping ? 64 : 59;
            return d.Action == SquadAction.PushNow ? 34 // Gogogo
                : d.Action == SquadAction.Suppressing ? 43 // Suppress
                : d.Action == SquadAction.SpreadOut ? 44 // Spreadout
                : d.Action == SquadAction.GetInCover ? 45 // GetInCover
                : d.Action == SquadAction.Following ? 59 : d.Action == SquadAction.Covering ? 60
                : d.Action == SquadAction.Advancing ? 33 : d.Action == SquadAction.Leading ? 31
                : d.Action == SquadAction.RequestCover ? 30 : d.Action == SquadAction.Rallying ? 47
                : d.Action == SquadAction.HoldTeam ? 36 : 32;
        }
        public static SquadCommand Command(int phrase)
            => phrase == 31 ? SquadCommand.Follow : phrase == 30 || phrase == 43 || phrase == 23 || phrase == 20 ? SquadCommand.Cover
                : phrase == 33 || phrase == 34 ? SquadCommand.Advance : phrase == 32 ? SquadCommand.Retreat
                : phrase == 47 ? SquadCommand.Regroup : phrase == 36 ? SquadCommand.Hold
                : SquadCommand.None;
        public static bool ReadyCall(int phrase) => phrase == 30 || phrase == 43 || phrase == 23 || phrase == 20;
        public static bool Tactical(SquadAction action) => action == SquadAction.Covering || action == SquadAction.Advancing
            || action == SquadAction.Retreating || action == SquadAction.RequestCover
            || action == SquadAction.PushNow || action == SquadAction.Suppressing
            || action == SquadAction.SpreadOut || action == SquadAction.GetInCover;
        public static bool ReportPhrase(int phrase)
            => phrase == 39 || phrase == 42 || phrase == 66 || phrase == 71
                || phrase == 69 || phrase == 70 || phrase == 72 || phrase == 73 || phrase == 74
                || phrase == 75 || phrase == 76 || phrase == 78 || phrase == 79
                || phrase == 34 || phrase == 43 || phrase == 44 || phrase == 45;
        public static float ReportDelay(int local, int peer0, int peer1, int peer2)
        {
            if (local <= 0) return 0.2f;
            int rank = (peer0 > 0 && peer0 < local ? 1 : 0)
                + (peer1 > 0 && peer1 < local ? 1 : 0)
                + (peer2 > 0 && peer2 < local ? 1 : 0);
            return 0.12f + rank * 0.18f;
        }
        public static bool ReportCurrent(int captured, int current) => captured < 0 || captured == current;
    }
}
