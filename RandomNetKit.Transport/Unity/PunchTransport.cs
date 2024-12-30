using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using Unity.Netcode;
using UnityEngine;
using RandomNetKit.Core.Shared;

namespace RandomNetKit.Transport.Unity;

/// <summary>
/// Unity Transport implementation that uses NAT punch-through for direct P2P connection.
/// If NAT punch fails, it will automatically fall back to relay server.
/// </summary>
public class PunchTransport : NetworkTransport, INetEventListener
{
    enum HostType
    {
        None,
        Server,
        Client
    }

    enum ConnectionState
    {
        Disconnected,
        ConnectingToPunchServer,
        WaitingForPunch,
        Punching,
        Connected,
        ConnectingToRelay
    }

    [Header("NAT Punch Settings")]
    [Tooltip("NAT Punch Server IP")]
    public string PunchServerAddress = "127.0.0.1";

    [Tooltip("NAT Punch Server Port")]
    public ushort PunchServerPort = 9051;

    [Header("Relay Fallback Settings")]
    [Tooltip("Relay Server IP")]
    public string RelayAddress = "127.0.0.1";

    [Tooltip("Relay Server Port")]
    public ushort RelayPort = 9050;

    [Header("Connection Settings")]
    [Tooltip("Base Key (会与 I_AM_HOST / I_AM_CLIENT 组合使用)")]
    public string BaseKey = "game";

    [Tooltip("Punch Attempt Timeout (sec)")]
    public float PunchTimeout = 5f;

    [Header("LiteNetLib Config")]
    [Tooltip("Ping Interval (sec)")]
    public float PingInterval = 1f;

    [Tooltip("Disconnect Timeout (sec)")]
    public float DisconnectTimeout = 5f;

    [Tooltip("Delay between connection attempts (sec)")]
    public float ReconnectDelay = 0.5f;

    [Tooltip("Max connection attempts")]
    public int MaxConnectAttempts = 10;

    public override ulong ServerClientId => 0;

    private NetManager _netManager;
    private HostType _hostType = HostType.None;
    private ConnectionState _connectionState = ConnectionState.Disconnected;
    private NetPeer _currentPeer;
    private ulong _localClientId;
    private float _punchStartTime;
    private IPEndPoint _targetEndPoint;
    private ulong _targetId;

    void Update()
    {
        _netManager?.PollEvents();

        // 检查NAT打洞超时
        if (_connectionState == ConnectionState.Punching && 
            Time.realtimeSinceStartup - _punchStartTime > PunchTimeout)
        {
            Debug.Log("[PunchTransport] NAT punch timeout, falling back to relay");
            FallbackToRelay();
        }
    }

    public override bool IsSupported => Application.platform != RuntimePlatform.WebGLPlayer;

    public override void Initialize(NetworkManager networkManager = null)
    {
        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            IPv6Enabled = false,
            PingInterval = SecondsToMilliseconds(PingInterval),
            DisconnectTimeout = SecondsToMilliseconds(DisconnectTimeout),
            ReconnectDelay = SecondsToMilliseconds(ReconnectDelay),
            MaxConnectAttempts = MaxConnectAttempts,
            UnconnectedMessagesEnabled = true,
            NatPunchEnabled = true
        };

        Debug.Log("[PunchTransport] Initialized");
    }

    public override bool StartClient()
    {
        if (_hostType != HostType.None)
        {
            throw new InvalidOperationException($"Already started as {_hostType}");
        }
        _hostType = HostType.Client;

        bool success = _netManager.Start();
        if (!success)
        {
            Debug.LogError("[PunchTransport] Failed to Start() NetManager!");
            return false;
        }

        // 首先连接到NAT打洞服务器
        _connectionState = ConnectionState.ConnectingToPunchServer;
        string fullKey = BaseKey + "|I_AM_CLIENT";
        Debug.Log($"[PunchTransport] StartClient -> Connect to Punch Server: {PunchServerAddress}:{PunchServerPort}");
        var peer = _netManager.Connect(PunchServerAddress, PunchServerPort, fullKey);

        if (peer == null)
        {
            Debug.LogError("[PunchTransport] Connect to Punch Server returned null peer!");
            return false;
        }

        return true;
    }

    public override bool StartServer()
    {
        if (_hostType != HostType.None)
        {
            throw new InvalidOperationException($"Already started as {_hostType}");
        }
        _hostType = HostType.Server;

        bool success = _netManager.Start();
        if (!success)
        {
            Debug.LogError("[PunchTransport] Failed to Start() NetManager!");
            return false;
        }

        // 首先连接到NAT打洞服务器
        _connectionState = ConnectionState.ConnectingToPunchServer;
        string fullKey = BaseKey + "|I_AM_HOST";
        Debug.Log($"[PunchTransport] StartServer -> Connect to Punch Server: {PunchServerAddress}:{PunchServerPort}");
        var peer = _netManager.Connect(PunchServerAddress, PunchServerPort, fullKey);

        if (peer == null)
        {
            Debug.LogError("[PunchTransport] Connect to Punch Server returned null peer!");
            return false;
        }

        return true;
    }

    public override void Shutdown()
    {
        if (_netManager != null)
        {
            _netManager.Stop();
            _netManager = null;
        }
        _hostType = HostType.None;
        _connectionState = ConnectionState.Disconnected;
        _currentPeer = null;
        Debug.Log("[PunchTransport] Shutdown");
    }

    public override void Send(ulong clientId, ArraySegment<byte> data, NetworkDelivery qos)
    {
        if (_currentPeer == null || _currentPeer.ConnectionState != ConnectionState.Connected)
        {
            Debug.LogWarning("[PunchTransport] Cannot send data. Peer is not connected.");
            return;
        }

        using var packet = new Packet(data, PacketType.Data);
        packet.Role = (_hostType == HostType.Server) ? Role.Host : Role.Client;
        packet.SenderId = _localClientId;
        packet.RecipientId = clientId;

        packet.Send(_currentPeer, ConvertDelivery(qos));
    }

    public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
    {
        clientId = 0;
        payload = default;
        receiveTime = Time.realtimeSinceStartup;
        return NetworkEvent.Nothing;
    }

    public override void DisconnectRemoteClient(ulong clientId)
    {
        _currentPeer?.Disconnect();
    }

    public override void DisconnectLocalClient()
    {
        _netManager?.DisconnectAll();
        _currentPeer = null;
        _connectionState = ConnectionState.Disconnected;
    }

    public override ulong GetCurrentRtt(ulong clientId)
    {
        return (_currentPeer != null) ? (ulong)_currentPeer.Ping * 2 : 0;
    }

    private void FallbackToRelay()
    {
        _connectionState = ConnectionState.ConnectingToRelay;
        _currentPeer?.Disconnect();
        _currentPeer = null;

        string fullKey = BaseKey + (_hostType == HostType.Server ? "|I_AM_HOST" : "|I_AM_CLIENT");
        Debug.Log($"[PunchTransport] Connecting to Relay: {RelayAddress}:{RelayPort}");
        _currentPeer = _netManager.Connect(RelayAddress, RelayPort, fullKey);
    }

    public void OnPeerConnected(NetPeer peer)
    {
        Debug.Log($"[PunchTransport] OnPeerConnected: {peer.EndPoint}");
        _currentPeer = peer;

        if (_connectionState == ConnectionState.ConnectingToPunchServer)
        {
            _connectionState = ConnectionState.WaitingForPunch;
            
            if (_hostType == HostType.Server)
            {
                // 服务器不需要主动发起打洞请求，等待客户端连接
                Debug.Log("[PunchTransport] Host connected to punch server, waiting for clients");
            }
        }
        else if (_connectionState == ConnectionState.Punching)
        {
            _connectionState = ConnectionState.Connected;
            Debug.Log("[PunchTransport] NAT punch successful!");
        }
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        Debug.Log($"[PunchTransport] OnPeerDisconnected: {disconnectInfo.Reason}");
        
        if (peer == _currentPeer)
        {
            _currentPeer = null;
            
            if (_connectionState == ConnectionState.Punching)
            {
                Debug.Log("[PunchTransport] NAT punch failed, falling back to relay");
                FallbackToRelay();
            }
            else
            {
                _connectionState = ConnectionState.Disconnected;
            }
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        Debug.LogWarning($"[PunchTransport] NetworkError: {socketError} @ {endPoint}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        try
        {
            using var packet = Packet.Deserialize(reader);
            float currentTime = Time.time;

            switch (packet.Type)
            {
                case PacketType.IdAssign:
                    HandleIdAssign(packet);
                    break;

                case PacketType.PunchInfo:
                    HandlePunchInfo(packet);
                    break;

                case PacketType.Data:
                    HandleData(packet, currentTime);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PunchTransport] OnNetworkReceive Exception: {ex}");
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleIdAssign(Packet packet)
    {
        _localClientId = packet.RecipientId;
        Debug.Log($"[PunchTransport] Received ID assignment: {_localClientId}");

        if (_hostType == HostType.Client)
        {
            // 客户端收到ID后，发送打洞请求
            using var punchRequest = new Packet(PacketType.PunchRequest)
            {
                Role = Role.Client,
                SenderId = _localClientId,
                RecipientId = 0 // Host ID always 0
            };
            punchRequest.Send(_currentPeer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandlePunchInfo(Packet packet)
    {
        var reader = new NetDataReader(packet.Data.ToArray());
        string address = reader.GetString();
        int port = reader.GetInt();
        _targetId = packet.RecipientId;

        _targetEndPoint = new IPEndPoint(IPAddress.Parse(address), port);
        Debug.Log($"[PunchTransport] Received punch info for {_targetId} @ {_targetEndPoint}");

        // 开始NAT打洞
        _connectionState = ConnectionState.Punching;
        _punchStartTime = Time.realtimeSinceStartup;
        
        // 断开与打洞服务器的连接
        _currentPeer.Disconnect();
        _currentPeer = null;

        // 尝试直接连接到目标
        _currentPeer = _netManager.Connect(_targetEndPoint, BaseKey);
    }

    private void HandleData(Packet packet, float currentTime)
    {
        InvokeOnTransportEvent(NetworkEvent.Data, packet.SenderId,
            new ArraySegment<byte>(packet.Data.ToArray()), currentTime);
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader,
        UnconnectedMessageType messageType)
    {
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
        request.Accept();
    }

    private static DeliveryMethod ConvertDelivery(NetworkDelivery type)
    {
        return type switch
        {
            NetworkDelivery.Unreliable => DeliveryMethod.Unreliable,
            NetworkDelivery.UnreliableSequenced => DeliveryMethod.Sequenced,
            NetworkDelivery.Reliable => DeliveryMethod.ReliableUnordered,
            NetworkDelivery.ReliableSequenced => DeliveryMethod.ReliableOrdered,
            NetworkDelivery.ReliableFragmentedSequenced => DeliveryMethod.ReliableOrdered,
            _ => DeliveryMethod.ReliableOrdered
        };
    }

    private static int SecondsToMilliseconds(float seconds)
    {
        return Mathf.CeilToInt(seconds * 1000f);
    }
} 