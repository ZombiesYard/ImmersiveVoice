using System;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Logging;
using EFT;
using UnityEngine;

namespace ImmersiveVoice
{
    // Optional Fika API adapter, resolved once. The host (including headless)
    // arbitrates only metadata; native Player.Say still carries every voice.
    internal sealed class FikaDialogueNetwork : IDisposable
    {
        private const int Hello = 0, Open = 1, Claim = 2, Grant = 3, Spoken = 4;
        private readonly FikaBridge _fika;
        private readonly ManualLogSource _log;
        private readonly DialogueArbiter _calls = new DialogueArbiter();
        private readonly EventInfo _event;
        private readonly Delegate _eventHandler;
        private readonly Type _packet, _managerType, _peerType;
        private readonly object _reliable;
        private readonly MethodInfo _register, _unregister, _send, _sendPeer;
        private readonly Func<int, object> _identity;
        private object _manager;
        private Func<IDictionary> _players;
        private bool _server, _failed, _registered;
        private int _epoch, _sequence;
        public bool Ready { get; private set; }
        public int Sent { get; private set; }
        public int Received { get; private set; }
        public int Grants { get; private set; }

        public FikaDialogueNetwork(Assembly assembly, FikaBridge fika, ManualLogSource log)
        {
            _fika = fika; _log = log;
            _identity = id => { Player p = Player(id); return p != null && p.HealthController?.IsAlive == true ? p : null; };
            Type Find(string name) => assembly.GetType("Fika.Core." + name, true);
            _packet = DialoguePacketFactory.Create(Find("Networking.LiteNetLib.Utils.INetSerializable"),
                Find("Networking.LiteNetLib.Utils.NetDataWriter"), Find("Networking.LiteNetLib.Utils.NetDataReader"));
            _managerType = Find("Networking.IFikaNetworkManager");
            _peerType = Find("Networking.LiteNetLib.NetPeer");
            _reliable = Enum.Parse(Find("Networking.LiteNetLib.DeliveryMethod"), "ReliableOrdered");
            _register = _managerType.GetMethods().Single(m => m.Name == "RegisterPacket" && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(_packet, _peerType);
            _unregister = _managerType.GetMethod("UnregisterPacket").MakeGenericMethod(_packet);
            _send = _managerType.GetMethod("SendData").MakeGenericMethod(_packet);
            _sendPeer = _managerType.GetMethod("SendDataToPeer").MakeGenericMethod(_packet);
            _event = Find("Modding.FikaEventDispatcher").GetEvent("OnFikaEvent");
            var argument = Expression.Parameter(_event.EventHandlerType.GetMethod("Invoke").GetParameters()[0].ParameterType);
            _eventHandler = Expression.Lambda(_event.EventHandlerType,
                Expression.Call(Expression.Constant(this), GetType().GetMethod(nameof(OnEvent), BindingFlags.NonPublic | BindingFlags.Instance),
                    Expression.Convert(argument, typeof(object))), argument).Compile();
            _event.AddEventHandler(null, _eventHandler);
        }

        private void OnEvent(object value)
        {
            try
            {
                string name = value.GetType().Name;
                object manager = (value.GetType().GetProperty("Manager") ?? value.GetType().GetProperty("NetworkManager"))?.GetValue(value);
                if (name == "FikaNetworkManagerDestroyedEvent") { if (ReferenceEquals(manager, _manager)) Reset(); return; }
                if (name != "FikaNetworkManagerCreatedEvent" && name != "PeerConnectedEvent" && name != "PeerDisconnectedEvent") return;
                if (manager == null) return;
                if (!ReferenceEquals(manager, _manager)) Attach(manager);
                if (_failed) return;
                object peer = value.GetType().GetProperty("Peer")?.GetValue(value);
                if (name == "PeerDisconnectedEvent")
                {
                    if (_server) _calls.RemoveOwner(peer);
                    else { Ready = false; _calls.Clear(); }
                    return;
                }
                if (_server && name == "PeerConnectedEvent" && peer != null) Send(New(Hello), peer);
            }
            catch (Exception ex) { Fail(ex); }
        }

        private void Attach(object manager)
        {
            Reset(); _manager = manager; _failed = false;
            Sent = Received = Grants = 0;
            _server = manager.GetType().Name == "FikaServer";
            _epoch = new System.Random().Next(1, int.MaxValue); _sequence = 0;
            var packet = Expression.Parameter(_packet); var peer = Expression.Parameter(_peerType);
            Delegate callback = Expression.Lambda(typeof(Action<,>).MakeGenericType(_packet, _peerType),
                Expression.Call(Expression.Constant(this), GetType().GetMethod(nameof(Receive), BindingFlags.NonPublic | BindingFlags.Instance),
                    Expression.Convert(packet, typeof(DialoguePacketData)), Expression.Convert(peer, typeof(object))), packet, peer).Compile();
            // Fika uses a ushort packet hash: refuse a collision instead of
            // replacing another mod's subscription.
            object processor = manager.GetType().GetField("_packetProcessor", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(manager);
            if (processor == null) throw new MissingFieldException("Fika packet processor");
            Type processorType = processor.GetType();
            object hash = processorType.GetMethod("GetShortHash", BindingFlags.NonPublic | BindingFlags.Instance).MakeGenericMethod(_packet).Invoke(processor, null);
            var callbacks = (IDictionary)processorType.GetField("_callbacks", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(processor);
            if (callbacks.Contains(hash)) throw new InvalidOperationException("Dialogue packet hash collision");
            _register.Invoke(manager, new object[] { callback }); _registered = true;
            var coop = Expression.Property(Expression.Convert(Expression.Constant(manager), _managerType), "CoopHandler");
            var players = Expression.PropertyOrField(coop, "Players");
            _players = Expression.Lambda<Func<IDictionary>>(Expression.Convert(players, typeof(IDictionary))).Compile();
            Ready = _server;
            _log.LogInfo("Squad dialogue protocol registered; " + (_server ? "host arbitration active." : "waiting for host capability."));
        }

        private DialoguePacketData New(int kind, DialogueTicket ticket = default, int actor = 0)
        {
            var data = (DialoguePacketData)Activator.CreateInstance(_packet);
            data.Protocol = 1; data.Kind = kind; data.Caller = ticket.Caller; data.Epoch = ticket.Epoch;
            data.Sequence = ticket.Sequence; data.Actor = actor;
            return data;
        }
        private Player Player(int id) => _players?.Invoke()?[id] as Player;
        private bool Human(Player player) => player != null && !_fika.IsAI(player) && player.HealthController?.IsAlive == true && !_fika.Downed(player);

        private void Receive(DialoguePacketData data, object peer)
        {
            if (_failed || data.Protocol != 1) return;
            try
            {
                Received++;
                if (!_server && data.Kind == Hello) { Ready = true; return; }
                var ticket = new DialogueTicket { Caller = data.Caller, Epoch = data.Epoch, Sequence = data.Sequence };
                float now = Time.time;
                if (data.Kind == Open) _calls.Reconcile(_identity);
                if (_server)
                {
                    Player caller = Player(data.Caller), actor = Player(data.Actor);
                    if (!Human(caller) || !Human(actor) || caller.GameWorld != actor.GameWorld
                        || caller != actor && !ViewProbe.IsAlly(caller, actor)) return;
                    if (data.Kind == Open && data.Actor == data.Caller && SquadDialogue.Command(data.Phrase) != SquadCommand.None
                        && _calls.Open(ticket, data.Phrase, data.Clip, data.DurationMs / 1000f, now, peer ?? this, caller))
                    { Send(data); return; }
                    if (data.Kind == Claim && _calls.Claim(ticket, data.Actor, now))
                    { Grants++; Send(New(Grant, ticket, data.Actor)); }
                    if (data.Kind == Spoken && _calls.Reply(ticket, data.Actor, data.Phrase, data.Clip, data.DurationMs / 1000f, now))
                        Send(data);
                }
                else if (data.Kind == Open && Human(Player(data.Caller)))
                    _calls.Open(ticket, data.Phrase, data.Clip, data.DurationMs / 1000f, now, identity: Player(data.Caller));
                else if (data.Kind == Grant) _calls.Claim(ticket, data.Actor, now);
                else if (data.Kind == Spoken && Human(Player(data.Actor)))
                    _calls.Reply(ticket, data.Actor, data.Phrase, data.Clip, data.DurationMs / 1000f, now);
            }
            catch (Exception ex) { Fail(ex); }
        }

        public DialogueTicket Publish(int caller, int phrase, int clip, float duration)
        {
            if (!Ready || _failed || SquadDialogue.Command(phrase) == SquadCommand.None || float.IsNaN(duration) || duration <= 0 || duration > 20) return default;
            var ticket = new DialogueTicket { Caller = caller, Epoch = _epoch, Sequence = ++_sequence };
            var data = New(Open, ticket, caller); data.Phrase = phrase; data.Clip = clip; data.DurationMs = (int)(duration * 1000f);
            if (_server) Receive(data, null); else Send(data);
            return Ready ? ticket : default;
        }
        public void ReplySpoken(DialogueTicket ticket, int actor, int phrase, int clip, float duration)
        {
            if (!Ready || _failed || !ticket.Exists || phrase != 60 || Winner(ticket, Time.time) != actor
                || float.IsNaN(duration) || duration <= 0 || duration > 20) return;
            var data = New(Spoken, ticket, actor); data.Phrase = phrase; data.Clip = clip; data.DurationMs = (int)(duration * 1000f);
            if (_server) Receive(data, null); else Send(data);
        }
        public DialogueTicket MatchReply(int caller, int actor, int clip, float heardAt, float now)
            => Ready ? _calls.MatchReply(caller, actor, clip, heardAt, now) : default;
        public DialogueTicket Match(int caller, int phrase, int clip, float heardAt, float now)
            => Ready ? _calls.Match(caller, phrase, clip, heardAt, now) : default;
        public int Winner(DialogueTicket ticket, float now) => Ready ? _calls.Winner(ticket, now) : -1;
        public void Request(DialogueTicket ticket, int actor)
        {
            if (!Ready || _failed || !ticket.Exists) return;
            var data = New(Claim, ticket, actor);
            if (_server) Receive(data, null); else Send(data);
        }
        private void Send(DialoguePacketData data, object peer = null)
        {
            if (_manager == null || _failed) return;
            try
            {
                if (peer == null) _send.Invoke(_manager, new object[] { data, _reliable, false });
                else _sendPeer.Invoke(_manager, new object[] { data, _reliable, peer });
                Sent++;
            }
            catch (Exception ex) { Fail(ex); }
        }
        private void Fail(Exception ex)
        {
            Ready = false; _failed = true;
            _log.LogWarning("Squad dialogue arbitration unavailable; responses remain silent. " + FaultDetails.Format(ex));
        }
        private void Reset()
        {
            if (_manager != null) _log.LogInfo($"Squad dialogue transport stopped. Sent={Sent} Received={Received} Grants={Grants}");
            Ready = false; _calls.Clear(); _players = null;
            if (_manager != null && _registered) { try { _unregister.Invoke(_manager, null); } catch { /* manager may already be disposed */ } }
            _manager = null; _registered = false;
        }
        public void Dispose() { _event.RemoveEventHandler(null, _eventHandler); Reset(); }
    }
}
