using System;
using System.Collections.Generic;
using System.Reflection;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using ImmersiveVoice;

namespace ImmersiveVoice
{
    internal static class ViewProbe
    { public static bool IsAlly(EFT.Player a, EFT.Player b) => a.GroupId == b.GroupId; }
}
namespace Fika.Core.Networking.LiteNetLib
{
    public enum DeliveryMethod { ReliableOrdered }
    public sealed class NetPeer { public Fika.Core.Networking.FikaClient Client; }
}
namespace Fika.Core.Networking.LiteNetLib.Utils
{
    public interface INetSerializable { void Serialize(NetDataWriter writer); void Deserialize(NetDataReader reader); }
    public sealed class NetDataWriter
    { public readonly List<int> Values = new List<int>(); public void Put(int value) => Values.Add(value); }
    public sealed class NetDataReader
    { public int[] Values; private int _index; public int GetInt() => Values[_index++]; }
    public class NetPacketProcessor
    {
        private readonly Dictionary<ushort, Action<int[], object>> _callbacks = new Dictionary<ushort, Action<int[], object>>();
        protected ushort GetShortHash<T>() => Hash(typeof(T));
        public static ushort Hash(Type type)
        {
            ushort hash = ushort.MaxValue;
            foreach (char c in type.ToString())
            { hash ^= (ushort)(c << 8); for (int i = 0; i < 8; i++) hash = (ushort)((hash & 0x8000) == 0 ? hash << 1 : (hash << 1) ^ 0x1021); }
            return hash;
        }
        public void Register<T, U>(Action<T, U> callback) where T : INetSerializable, new()
        {
            T packet = new T();
            _callbacks[GetShortHash<T>()] = (values, owner) => { packet.Deserialize(new NetDataReader { Values = values }); callback(packet, (U)owner); };
        }
        public void Unregister<T>() where T : INetSerializable => _callbacks.Remove(GetShortHash<T>());
        public void Receive(ushort hash, int[] data, object peer) => _callbacks[hash](data, peer);
        public int Count => _callbacks.Count;
    }
}
namespace Fika.Core.Main.Components
{
    public sealed class CoopHandler
    { public Dictionary<int, Fika.Core.Main.Players.FikaPlayer> Players { get; set; } = new Dictionary<int, Fika.Core.Main.Players.FikaPlayer>(); }
}
namespace Fika.Core.Networking
{
    public interface IFikaNetworkManager
    {
        Fika.Core.Main.Components.CoopHandler CoopHandler { get; }
        void RegisterPacket<T, U>(Action<T, U> callback) where T : INetSerializable, new();
        void UnregisterPacket<T>() where T : INetSerializable;
        void SendData<T>(ref T packet, DeliveryMethod delivery, bool broadcast = false) where T : INetSerializable;
        void SendDataToPeer<T>(ref T packet, DeliveryMethod delivery, NetPeer peer) where T : INetSerializable;
    }
    public abstract class TestManager : IFikaNetworkManager
    {
        protected NetPacketProcessor _packetProcessor = new NetPacketProcessor();
        public NetPacketProcessor Processor => _packetProcessor;
        public Fika.Core.Main.Components.CoopHandler CoopHandler { get; set; }
        public NetPeer Peer;
        public void RegisterPacket<T, U>(Action<T, U> callback) where T : INetSerializable, new() => _packetProcessor.Register(callback);
        public void UnregisterPacket<T>() where T : INetSerializable => _packetProcessor.Unregister<T>();
        public void SendData<T>(ref T packet, DeliveryMethod delivery, bool broadcast = false) where T : INetSerializable
        {
            var writer = new NetDataWriter(); packet.Serialize(writer);
            ushort hash = NetPacketProcessor.Hash(typeof(T)); int[] data = writer.Values.ToArray();
            if (this is FikaServer)
                foreach (FikaClient client in TestWire.Clients) TestWire.Pending.Enqueue(() => client.Processor.Receive(hash, data, null));
            else TestWire.Pending.Enqueue(() => TestWire.Server.Processor.Receive(hash, data, Peer));
        }
        public void SendDataToPeer<T>(ref T packet, DeliveryMethod delivery, NetPeer peer) where T : INetSerializable
        {
            var writer = new NetDataWriter(); packet.Serialize(writer);
            ushort hash = NetPacketProcessor.Hash(typeof(T)); int[] data = writer.Values.ToArray();
            TestWire.Pending.Enqueue(() => peer.Client.Processor.Receive(hash, data, null));
        }
    }
    public sealed class FikaServer : TestManager { }
    public sealed class FikaClient : TestManager { }
    public static class TestWire
    {
        public static FikaServer Server;
        public static readonly List<FikaClient> Clients = new List<FikaClient>();
        public static readonly Queue<Action> Pending = new Queue<Action>();
        public static void Pump() { int count = 0; while (Pending.Count > 0) { Pending.Dequeue()(); if (++count > 100) throw new Exception("network loop"); } }
    }
}
namespace Fika.Core.Modding.Events
{
    public class FikaEvent { }
    public sealed class FikaNetworkManagerCreatedEvent : FikaEvent
    { public Fika.Core.Networking.IFikaNetworkManager Manager { get; set; } }
    public sealed class FikaNetworkManagerDestroyedEvent : FikaEvent
    { public Fika.Core.Networking.IFikaNetworkManager Manager { get; set; } }
    public sealed class PeerConnectedEvent : FikaEvent
    { public Fika.Core.Networking.IFikaNetworkManager NetworkManager { get; set; } public NetPeer Peer { get; set; } }
    public sealed class PeerDisconnectedEvent : FikaEvent
    { public Fika.Core.Networking.IFikaNetworkManager NetworkManager { get; set; } public NetPeer Peer { get; set; } }
}
namespace Fika.Core.Modding
{
    public static class FikaEventDispatcher
    {
        public delegate void FikaEventHandler(Events.FikaEvent e);
        public static event FikaEventHandler OnFikaEvent;
        public static void Dispatch(Events.FikaEvent e) => OnFikaEvent?.Invoke(e);
    }
}

internal static class DialogueNetworkScenarios
{
    private static void Event(FikaDialogueNetwork endpoint, object e)
        => typeof(FikaDialogueNetwork).GetMethod("OnEvent", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(endpoint, new[] { e });
    public static void Run(Action<bool, string> check, FikaBridge bridge)
    {
        var log = new BepInEx.Logging.ManualLogSource();
        var assembly = typeof(Fika.Core.Networking.IFikaNetworkManager).Assembly;
        var coop = new Fika.Core.Main.Components.CoopHandler(); object world = new object();
        for (int i = 2; i <= 5; i++) coop.Players[i] = new Fika.Core.Main.Players.FikaPlayer { NetId = i, GameWorld = world };
        var server = new Fika.Core.Networking.FikaServer { CoopHandler = coop };
        Fika.Core.Networking.TestWire.Server = server;
        var host = new FikaDialogueNetwork(assembly, bridge, log);
        Event(host, new Fika.Core.Modding.Events.FikaNetworkManagerCreatedEvent { Manager = server });
        check(host.Ready, "headless-style host registers metadata arbitration without a voice session");
        var clients = new FikaDialogueNetwork[4];
        for (int i = 0; i < 4; i++)
        {
            var manager = new Fika.Core.Networking.FikaClient { CoopHandler = coop };
            manager.Peer = new NetPeer { Client = manager };
            Fika.Core.Networking.TestWire.Clients.Add(manager);
            clients[i] = new FikaDialogueNetwork(assembly, bridge, log);
            Event(clients[i], new Fika.Core.Modding.Events.FikaNetworkManagerCreatedEvent { Manager = manager });
            clients[i].Publish(i + 2, 31, 3, 1);
            check(!clients[i].Ready && clients[i].Sent == 0, "client sends no unknown custom packets before host capability, client " + i);
            Event(host, new Fika.Core.Modding.Events.PeerConnectedEvent { NetworkManager = server, Peer = manager.Peer });
        }
        Fika.Core.Networking.TestWire.Pump();
        check(Array.TrueForAll(clients, c => c.Ready), "all four clients receive the host handshake via PeerConnectedEvent.NetworkManager");
        UnityEngine.Time.time = 10;
        clients[0].Publish(2, 31, 3, 1); Fika.Core.Networking.TestWire.Pump();
        var ticket = clients[1].Match(2, 31, 3, 10, 10.1f);
        check(ticket.Exists, "serialized host-relayed call metadata matches independently heard native clip");
        for (int i = 1; i < 4; i++) clients[i].Request(ticket, i + 2);
        Fika.Core.Networking.TestWire.Pump();
        check(host.Grants == 1 && Array.TrueForAll(clients, c => c.Winner(ticket, 11) == 3),
            "three simultaneous eligible responders receive exactly one consistent host grant");
        clients[2].Request(ticket, 4); clients[1].Request(ticket, 3); Fika.Core.Networking.TestWire.Pump();
        check(host.Grants == 1, "duplicates and loser retries cannot obtain a second reply");
        UnityEngine.Time.time = 30;
        clients[3].Request(ticket, 5); Fika.Core.Networking.TestWire.Pump();
        check(host.Grants == 1 && clients[3].Winner(ticket, 30) == -1, "late network packets cannot revive an expired conversation");
        coop.Players[2].IsObservedAI = true;
        clients[0].Publish(2, 31, 4, 1); Fika.Core.Networking.TestWire.Pump();
        check(!clients[1].Match(2, 31, 4, 30, 30).Exists, "host rejects replicated AI actors before opening a human dialogue");
        coop.Players[2].IsObservedAI = false;
        clients[0].Publish(2, 31, 5, 1); Fika.Core.Networking.TestWire.Pump();
        ticket = clients[1].Match(2, 31, 5, 30, 30);
        coop.Players[3].GroupId = "different";
        clients[1].Request(ticket, 3); Fika.Core.Networking.TestWire.Pump();
        check(host.Winner(ticket, 30) == 0, "host rejects an unrelated squad's reply claim");
        coop.Players[3].GroupId = "group";
        Event(host, new Fika.Core.Modding.Events.PeerDisconnectedEvent { NetworkManager = server, Peer = Fika.Core.Networking.TestWire.Clients[0].Peer });
        clients[1].Request(ticket, 3); Fika.Core.Networking.TestWire.Pump();
        check(host.Grants == 1, "caller disconnect invalidates pending reply claims");
        var oldClient = clients[0]; var rejoined = Fika.Core.Networking.TestWire.Clients[0];
        oldClient.Dispose();
        coop.Players[2] = new Fika.Core.Main.Players.FikaPlayer { NetId = 2, GameWorld = world };
        clients[0] = new FikaDialogueNetwork(assembly, bridge, log);
        Event(clients[0], new Fika.Core.Modding.Events.FikaNetworkManagerCreatedEvent { Manager = rejoined });
        Event(host, new Fika.Core.Modding.Events.PeerConnectedEvent { NetworkManager = server, Peer = rejoined.Peer });
        Fika.Core.Networking.TestWire.Pump();
        clients[0].Publish(2, 31, 6, 1); Fika.Core.Networking.TestWire.Pump();
        ticket = clients[1].Match(2, 31, 6, 30, 30);
        clients[1].Request(ticket, 3); Fika.Core.Networking.TestWire.Pump();
        check(ticket.Exists && host.Grants == 2 && clients[1].Winner(ticket, 30) == 3,
            "same-ID reconnect rebinds the caller on every client and permits a new response");
        Event(clients[0], new Fika.Core.Modding.Events.PeerDisconnectedEvent { NetworkManager = rejoined, Peer = rejoined.Peer });
        int sent = clients[0].Sent;
        clients[0].Publish(2, 31, 7, 1);
        check(!clients[0].Ready && clients[0].Sent == sent && clients[0].Winner(ticket, 30) == -1,
            "server disconnect clears client capability and forbids sends until a new handshake");
        Event(host, new Fika.Core.Modding.Events.PeerConnectedEvent { NetworkManager = server, Peer = rejoined.Peer });
        Fika.Core.Networking.TestWire.Pump(); UnityEngine.Time.time = 40;
        var coverCalls = new DialogueTicket[4];
        for (int i = 0; i < 4; i++) coverCalls[i] = clients[i].Publish(i + 2, 23, 100 + i, 1);
        Fika.Core.Networking.TestWire.Pump();
        for (int i = 0; i < 4; i++) clients[(i + 1) % 4].Request(coverCalls[i], (i + 1) % 4 + 2);
        Fika.Core.Networking.TestWire.Pump(); UnityEngine.Time.time = 41;
        for (int i = 0; i < 4; i++)
        {
            int winner = (i + 1) % 4 + 2;
            check(!clients[i].MatchReply(i + 2, winner, 200 + i, 41, 41).Exists, "grant alone supplies no playback receipt, caller " + i);
            clients[(i + 2) % 4].ReplySpoken(coverCalls[i], (i + 2) % 4 + 2, 60, 200 + i, 1);
            clients[(i + 1) % 4].ReplySpoken(coverCalls[i], winner, 60, 200 + i, 1);
        }
        Fika.Core.Networking.TestWire.Pump();
        for (int i = 0; i < 4; i++)
        {
            int winner = (i + 1) % 4 + 2;
            check(clients[i].MatchReply(i + 2, winner, 200 + i, 41, 41.2f).Same(coverCalls[i]), "host relays the winning Covering receipt for its exact call, caller " + i);
            check(!clients[i].MatchReply(i + 2, winner, 201 + i, 41, 41.2f).Exists, "other native clip cannot satisfy the relayed receipt, caller " + i);
        }
        check(!clients[0].Publish(2, 63, 300, 1).Exists, "Ready is terminal and emits no new call packet");
        for (int i = 0; i < 4; i++)
        {
            var manager = Fika.Core.Networking.TestWire.Clients[i];
            Event(clients[i], new Fika.Core.Modding.Events.FikaNetworkManagerDestroyedEvent { Manager = manager });
            check(!clients[i].Ready && manager.Processor.Count == 0, "network teardown unregisters callbacks and revokes tickets, client " + i);
            clients[i].Dispose();
        }
        host.Dispose(); Fika.Core.Networking.TestWire.Clients.Clear();
        check(server.Processor.Count == 0 && log.Warnings == 0, "transport fixtures complete without warnings or retained subscriptions");
    }
}
