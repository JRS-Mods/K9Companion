using System;
using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace K9Companion.Fika
{
    public struct DogHelloPacket : INetSerializable
    {
        public string Version;
        public bool Welcome;

        public readonly void Serialize(NetDataWriter writer) { writer.Put(Version ?? ""); writer.Put(Welcome); }
        public void Deserialize(NetDataReader reader) { Version = reader.GetString(); Welcome = reader.GetBool(); }
    }

    public struct DogStatePacket : INetSerializable
    {
        internal DogState State;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(State.Owner);
            writer.PutUnmanaged(State.Position);
            writer.Put(State.Yaw); writer.Put(State.Pitch); writer.Put(State.LookYaw); writer.Put(State.LookPitch);
            writer.Put(State.LayerWeight); writer.Put(State.Playback);
            writer.Put(State.Clip); writer.Put(State.Layer); writer.Put(State.Starts);
            byte flags = (byte)((State.Looping ? 1 : 0) | (State.Walking ? 2 : 0) | (State.Low ? 4 : 0) | (State.Resting ? 8 : 0) | (State.Targetable ? 16 : 0) | (State.Dead ? 32 : 0));
            writer.Put(flags);
        }

        public void Deserialize(NetDataReader reader)
        {
            State.Owner = reader.GetInt();
            State.Position = reader.GetUnmanaged<Vector3>();
            State.Yaw = reader.GetFloat(); State.Pitch = reader.GetFloat(); State.LookYaw = reader.GetFloat(); State.LookPitch = reader.GetFloat();
            State.LayerWeight = reader.GetFloat(); State.Playback = reader.GetFloat();
            State.Clip = reader.GetShort(); State.Layer = reader.GetShort(); State.Starts = reader.GetByte();
            byte flags = reader.GetByte();
            State.Looping = (flags & 1) != 0; State.Walking = (flags & 2) != 0; State.Low = (flags & 4) != 0;
            State.Resting = (flags & 8) != 0; State.Targetable = (flags & 16) != 0; State.Dead = (flags & 32) != 0;
        }
    }

    public struct DogLooksPacket : INetSerializable
    {
        internal DogLooks Looks;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(Looks.Owner); writer.Put(Looks.Vest ?? ""); writer.Put(Looks.Head ?? ""); writer.Put(Looks.Collar ?? "");
            writer.Put(Looks.Camera); writer.Put(Looks.Scale);
        }

        public void Deserialize(NetDataReader reader)
        {
            Looks.Owner = reader.GetInt();
            Looks.Vest = Named(reader.GetString()); Looks.Head = Named(reader.GetString()); Looks.Collar = Named(reader.GetString());
            Looks.Camera = reader.GetBool(); Looks.Scale = reader.GetFloat();
        }

        private static string Named(string text) => string.IsNullOrEmpty(text) ? null : text;
    }

    public struct DogNotePacket : INetSerializable
    {
        public byte Note;
        public int Owner;
        public string Text;
        public float Amount;
        public Vector3 At;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(Note); writer.Put(Owner); writer.Put(Text ?? ""); writer.Put(Amount); writer.PutUnmanaged(At);
        }

        public void Deserialize(NetDataReader reader)
        {
            Note = reader.GetByte(); Owner = reader.GetInt(); Text = reader.GetString(); Amount = reader.GetFloat(); At = reader.GetUnmanaged<Vector3>();
        }
    }

    /// <summary>
    /// The dog's messages over Fika's connection. Clients talk to the host, which passes on what the others need, and only
    /// to players who've said they have this mod (a game without it can't read these and would log an error for each one).
    /// That's why nothing goes out until the host has answered the hello.
    /// </summary>
    internal sealed class FikaLink : IDogLink
    {
        private const int HelloTries = 3;
        private const float HelloEvery = 4f;

        private readonly HashSet<NetPeer> peers = new HashSet<NetPeer>();
        private readonly Dictionary<int, NetPeer> owners = new Dictionary<int, NetPeer>();
        private IFikaNetworkManager network;
        private bool host, welcomed;
        private int helloTries;
        private float nextHello;

        public FikaLink()
        {
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(e => Safely(() => Created(e)));
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerDestroyedEvent>(e => Safely(() => Destroyed(e)));
            FikaEventDispatcher.SubscribeEvent<PeerDisconnectedEvent>(e => Safely(() => Left(e)));
            // Whether or not you have a dog yourself: you want to see the others'.
            FikaEventDispatcher.SubscribeEvent<GameWorldStartedEvent>(_ => Safely(() => { helloTries = 0; nextHello = 0; if (network != null && !host) SayHello(); }));
        }

        // These run inside Fika's own events and its network loop: anything going wrong with the dog must stop here.
        private static void Safely(Action act)
        {
            try { act(); }
            catch (Exception e) { Plugin.Log.LogWarning("Dog sync: " + e); }
        }

        public bool Online
        {
            get
            {
                if (network == null) return false;
                if (host || welcomed) return true;
                SayHello();
                return false;
            }
        }

        public bool Headless => FikaBackendUtils.IsHeadless;

        public bool InGroup
        {
            get
            {
                if (!FikaBackendUtils.IsServer && !FikaBackendUtils.IsClient) return false;
                // Joined someone else's raid (a headless host is nobody): there's someone else, whatever the count says yet.
                if (FikaBackendUtils.IsClient && !FikaBackendUtils.IsHeadlessGame) return true;
                if (network == null) return false;
                // The count includes a headless host, which doesn't play.
                return network.PlayerAmount - (FikaBackendUtils.IsHeadless || FikaBackendUtils.IsHeadlessGame ? 1 : 0) > 1;
            }
        }

        public bool IsRemoteBot(Player player) => player is ObservedPlayer copy && copy.IsObservedAI;

        public int NetIdOf(Player player) => player is FikaPlayer fika ? fika.NetId : -1;

        public Player PlayerOf(int netId)
        {
            Dictionary<int, FikaPlayer> players = network?.CoopHandler?.Players;
            return players != null && players.TryGetValue(netId, out FikaPlayer player) ? player : null;
        }

        private void Created(FikaNetworkManagerCreatedEvent e)
        {
            Reset();
            network = e.Manager;
            host = network is FikaServer;
            network.RegisterPacket<DogHelloPacket, NetPeer>((p, from) => Safely(() => Hello(p, from)));
            network.RegisterPacket<DogStatePacket, NetPeer>((p, from) => Safely(() => State(p, from)));
            network.RegisterPacket<DogLooksPacket, NetPeer>((p, from) => Safely(() => Looks(p, from)));
            network.RegisterPacket<DogNotePacket, NetPeer>((p, from) => Safely(() => Note(p, from)));
        }

        private void Destroyed(FikaNetworkManagerDestroyedEvent e)
        {
            if (e.Manager != network) return;
            network.UnregisterPacket<DogHelloPacket>();
            network.UnregisterPacket<DogStatePacket>();
            network.UnregisterPacket<DogLooksPacket>();
            network.UnregisterPacket<DogNotePacket>();
            Reset();
            DogNet.RaidOver();
        }

        private void Left(PeerDisconnectedEvent e)
        {
            if (e.NetworkManager != network) return;
            peers.Remove(e.Peer);
            var gone = new List<int>();
            foreach (KeyValuePair<int, NetPeer> owner in owners) if (owner.Value == e.Peer) gone.Add(owner.Key);
            foreach (int id in gone) { owners.Remove(id); DogNet.Received(DogNote.Gone, id, null, 0, default); }
        }

        private void Reset()
        {
            network = null; host = welcomed = false; helloTries = 0; nextHello = 0;
            peers.Clear(); owners.Clear();
        }

        // Only once a raid's really on (the host may still be loading before that), and only a few times: a host without the
        // mod just never answers.
        private void SayHello()
        {
            if (helloTries >= HelloTries || Time.time < nextHello) return;
            helloTries++; nextHello = Time.time + HelloEvery;
            var hello = new DogHelloPacket { Version = Plugin.Version };
            network.SendData(ref hello, DeliveryMethod.ReliableOrdered);
        }

        private void Hello(DogHelloPacket packet, NetPeer from)
        {
            if (host)
            {
                if (packet.Version != Plugin.Version) { Plugin.Log.LogWarning($"A player has K9 Companion {packet.Version}, this host {Plugin.Version}: their dog isn't shared."); return; }
                peers.Add(from);
                var welcome = new DogHelloPacket { Version = Plugin.Version, Welcome = true };
                network.SendDataToPeer(ref welcome, DeliveryMethod.ReliableOrdered, from);
            }
            else if (packet.Welcome) { welcomed = true; Plugin.Log.LogInfo("The host has K9 Companion too: dogs are shared."); }
        }

        private void Relay<T>(ref T packet, DeliveryMethod how, NetPeer except) where T : INetSerializable
        {
            foreach (NetPeer peer in peers)
                if (peer != except) network.SendDataToPeer(ref packet, how, peer);
        }

        private void Out<T>(ref T packet, DeliveryMethod how) where T : INetSerializable
        {
            if (host) Relay(ref packet, how, null);
            else network.SendData(ref packet, how);
        }

        public void Send(DogState state)
        {
            var packet = new DogStatePacket { State = state };
            Out(ref packet, DeliveryMethod.Sequenced);
        }

        public void Send(DogLooks looks)
        {
            var packet = new DogLooksPacket { Looks = looks };
            Out(ref packet, DeliveryMethod.ReliableOrdered);
        }

        public void Send(DogNote note, int owner, string text, float amount, Vector3 at)
        {
            var packet = new DogNotePacket { Note = (byte)note, Owner = owner, Text = text, Amount = amount, At = at };
            if (!host) { network.SendData(ref packet, DeliveryMethod.ReliableOrdered); return; }
            // On the host: for its own bots, handled here; a sighting goes only to that dog's owner.
            switch (note)
            {
                case DogNote.Bitten: case DogNote.AskHostile:
                    Handle(packet, null);
                    break;
                case DogNote.Spotted:
                    if (owners.TryGetValue(owner, out NetPeer ownerPeer)) network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, ownerPeer);
                    else DogNet.Received(note, owner, text, amount, at);
                    break;
                default:
                    Relay(ref packet, DeliveryMethod.ReliableOrdered, null);
                    break;
            }
        }

        private void State(DogStatePacket packet, NetPeer from)
        {
            if (host)
            {
                if (!peers.Contains(from)) return;
                owners[packet.State.Owner] = from;
                Relay(ref packet, DeliveryMethod.Sequenced, from);
            }
            DogNet.Received(packet.State);
        }

        private void Looks(DogLooksPacket packet, NetPeer from)
        {
            if (host)
            {
                if (!peers.Contains(from)) return;
                Relay(ref packet, DeliveryMethod.ReliableOrdered, from);
            }
            DogNet.Received(packet.Looks);
        }

        private void Note(DogNotePacket packet, NetPeer from)
        {
            if (host && !peers.Contains(from)) return;
            Handle(packet, from);
        }

        private void Handle(DogNotePacket packet, NetPeer from)
        {
            var note = (DogNote)packet.Note;
            if (host)
            {
                switch (note)
                {
                    case DogNote.AskHostile:
                        var answer = new DogNotePacket
                        {
                            Note = (byte)(DogNet.Answer(packet.Owner, packet.Text) ? DogNote.Hostile : DogNote.NotHostile),
                            Owner = packet.Owner, Text = packet.Text
                        };
                        if (from != null) network.SendDataToPeer(ref answer, DeliveryMethod.ReliableOrdered, from);
                        else DogNet.Received((DogNote)answer.Note, answer.Owner, answer.Text, 0, default);
                        return;
                    case DogNote.Sound: case DogNote.Gone:
                        Relay(ref packet, DeliveryMethod.ReliableOrdered, from);
                        break;
                }
            }
            DogNet.Received(note, packet.Owner, packet.Text, packet.Amount, packet.At);
        }
    }
}
