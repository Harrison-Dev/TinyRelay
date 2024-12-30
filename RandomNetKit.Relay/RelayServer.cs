using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using RandomNetKit.Core.Shared;

namespace RandomNetKit.Relay;

public class RelayServer : INetEventListener
{
    private readonly NetManager _netManager;
    private readonly Dictionary<NetPeer, ulong> _peerToIdMap = new();
    private readonly Dictionary<ulong, NetPeer> _idToPeerMap = new();
    private ulong _nextClientId = 1;
    private const ulong hostStartId = 0;
    private readonly Dictionary<NetPeer, bool> _peerIsHost = new();

    public RelayServer()
    {
        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            IPv6Enabled = false,
            DisconnectTimeout = 5000
        };
    }

    public bool Start(int port)
    {
        return _netManager.Start(port);
    }

    public void Stop()
    {
        _netManager.Stop();
        _peerToIdMap.Clear();
        _idToPeerMap.Clear();
        _peerIsHost.Clear();
    }

    public void PollEvents()
    {
        _netManager.PollEvents();
    }

    public void OnPeerConnected(NetPeer peer)
    {
        bool isHost = _peerIsHost[peer];
        ulong assignedId;
        
        if (isHost)
        {
            assignedId = hostStartId;
            Console.WriteLine($"[OnPeerConnected] Host connected. Assigned ID: {assignedId}");
        }
        else
        {
            assignedId = _nextClientId++;
            Console.WriteLine($"[OnPeerConnected] Client connected. Assigned ID: {assignedId}");
        }

        _peerToIdMap[peer] = assignedId;
        _idToPeerMap[assignedId] = peer;

        SendIdAssignment(peer, assignedId, isHost);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (_peerToIdMap.TryGetValue(peer, out ulong id))
        {
            Console.WriteLine($"[OnPeerDisconnected] Peer {id} disconnected: {disconnectInfo.Reason}");
            _peerToIdMap.Remove(peer);
            _idToPeerMap.Remove(id);
            _peerIsHost.Remove(peer);

            // Notify others about the disconnection
            using var notifyPacket = new Packet(PacketType.NotifyLeave)
            {
                Role = Role.Relay,
                SenderId = id
            };

            foreach (var targetPeer in _peerToIdMap.Keys)
            {
                notifyPacket.WithRecipient(_peerToIdMap[targetPeer]);
                notifyPacket.Send(targetPeer, DeliveryMethod.ReliableUnordered);
            }
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        Console.WriteLine($"[NetworkError] {socketError} @ {endPoint}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        try
        {
            using Packet receivedPacket = Packet.Deserialize(reader);
            if (receivedPacket.RecipientId == ulong.MaxValue)
            {
                foreach (var targetPeer in _peerToIdMap.Keys)
                {
                    if (targetPeer != peer)
                    {
                        receivedPacket.Clone().Send(targetPeer, deliveryMethod);
                    }
                }
            }
            else
            {
                if (_idToPeerMap.TryGetValue(receivedPacket.RecipientId, out var targetPeer))
                {
                    receivedPacket.Clone().Send(targetPeer, deliveryMethod);
                }
                else
                {
                    Console.WriteLine($"[NetworkReceive] Recipient not found: {receivedPacket.RecipientId}. Discard.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NetworkReceive] Exception: {ex.Message}");
        }
        finally
        {
            reader.Recycle();
        }
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // ignore
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // ignore
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        var reader = request.Data;
        byte[] data = new byte[reader.AvailableBytes];
        reader.GetBytes(data, 0, data.Length);
        string key = Encoding.UTF8.GetString(data);
        bool isHost = key.Contains("I_AM_HOST");
        var peer = request.Accept();
        _peerIsHost[peer] = isHost;
    }

    private void SendIdAssignment(NetPeer peer, ulong assignedId, bool isHost)
    {
        using var assignIdPacket = new Packet(PacketType.IdAssign)
        {
            Role = Role.Relay,
            SenderId = hostStartId,
            RecipientId = assignedId
        };

        assignIdPacket.Send(peer, DeliveryMethod.ReliableUnordered);

        using var notifyPacket = new Packet(PacketType.NotifyJoin)
        {
            Role = Role.Relay,
        };

        foreach (var targetPeer in _peerToIdMap.Keys)
        {
            if (targetPeer != peer)
            {
                notifyPacket.WithSender(assignedId);
                notifyPacket.WithRecipient(_peerToIdMap[targetPeer]);
                notifyPacket.Send(targetPeer, DeliveryMethod.ReliableUnordered);
            }
        }
    }
} 