using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using RandomNetKit.Core.Shared;

namespace RandomNetKit.PunchNAT;

public class PunchServer : INetEventListener
{
    private readonly NetManager _netManager;
    private readonly Dictionary<NetPeer, PeerInfo> _peerInfoMap = new();
    private readonly Dictionary<ulong, NetPeer> _idToPeerMap = new();
    private ulong _nextPeerId = 1;

    private class PeerInfo
    {
        public ulong Id { get; set; }
        public IPEndPoint ExternalEndPoint { get; set; }
        public bool IsHost { get; set; }
    }

    public PunchServer()
    {
        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            IPv6Enabled = false,
            DisconnectTimeout = 5000,
            UnconnectedMessagesEnabled = true, // 必须启用，用于NAT打洞
            NatPunchEnabled = true
        };
    }

    public bool Start(int port)
    {
        return _netManager.Start(port);
    }

    public void Stop()
    {
        _netManager.Stop();
        _peerInfoMap.Clear();
        _idToPeerMap.Clear();
    }

    public void PollEvents()
    {
        _netManager.PollEvents();
    }

    public void OnPeerConnected(NetPeer peer)
    {
        var peerInfo = new PeerInfo
        {
            Id = _nextPeerId++,
            ExternalEndPoint = peer.EndPoint,
            IsHost = false // 将通过连接请求数据设置
        };

        _peerInfoMap[peer] = peerInfo;
        _idToPeerMap[peerInfo.Id] = peer;

        Console.WriteLine($"[OnPeerConnected] Peer {peerInfo.Id} connected from {peer.EndPoint}");

        // 发送ID分配
        using var packet = new Packet(PacketType.IdAssign)
        {
            Role = Role.PunchServer,
            SenderId = 0,
            RecipientId = peerInfo.Id
        };
        packet.Send(peer, DeliveryMethod.ReliableOrdered);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (_peerInfoMap.TryGetValue(peer, out var peerInfo))
        {
            Console.WriteLine($"[OnPeerDisconnected] Peer {peerInfo.Id} disconnected: {disconnectInfo.Reason}");
            _idToPeerMap.Remove(peerInfo.Id);
            _peerInfoMap.Remove(peer);
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
            using var packet = Packet.Deserialize(reader);
            
            switch (packet.Type)
            {
                case PacketType.PunchRequest:
                    HandlePunchRequest(peer, packet);
                    break;
                    
                case PacketType.PunchFailed:
                    HandlePunchFailed(peer, packet);
                    break;
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

    private void HandlePunchRequest(NetPeer requestingPeer, Packet packet)
    {
        var requestingInfo = _peerInfoMap[requestingPeer];
        var targetId = packet.RecipientId;

        if (_idToPeerMap.TryGetValue(targetId, out var targetPeer))
        {
            var targetInfo = _peerInfoMap[targetPeer];

            // 向双方发送对方的endpoint信息
            SendPunchInfo(requestingPeer, targetPeer.EndPoint, targetInfo.Id);
            SendPunchInfo(targetPeer, requestingPeer.EndPoint, requestingInfo.Id);
        }
        else
        {
            Console.WriteLine($"[HandlePunchRequest] Target peer {targetId} not found");
        }
    }

    private void HandlePunchFailed(NetPeer peer, Packet packet)
    {
        var peerInfo = _peerInfoMap[peer];
        Console.WriteLine($"[HandlePunchFailed] NAT punch failed between {peerInfo.Id} and {packet.RecipientId}");
        // 这里可以添加额外的失败处理逻辑
    }

    private void SendPunchInfo(NetPeer peer, IPEndPoint targetEndPoint, ulong targetId)
    {
        using var packet = new Packet(PacketType.PunchInfo)
        {
            Role = Role.PunchServer,
            SenderId = 0,
            RecipientId = targetId
        };

        // 将目标endpoint信息序列化到数据包中
        var writer = new NetDataWriter();
        writer.Put(targetEndPoint.Address.ToString());
        writer.Put(targetEndPoint.Port);
        packet.WithData(writer.Data);

        packet.Send(peer, DeliveryMethod.ReliableOrdered);
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // 用于NAT打洞的未连接消息处理
        if (messageType == UnconnectedMessageType.NatMessage)
        {
            _netManager.NatPunchModule.ProcessMessage(remoteEndPoint, reader);
        }
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // 可以用来监控延迟
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        var reader = request.Data;
        byte[] data = new byte[reader.AvailableBytes];
        reader.GetBytes(data, 0, data.Length);
        string key = Encoding.UTF8.GetString(data);
        bool isHost = key.Contains("I_AM_HOST");
        
        var peer = request.Accept();
        if (_peerInfoMap.TryGetValue(peer, out var peerInfo))
        {
            peerInfo.IsHost = isHost;
        }
    }
} 