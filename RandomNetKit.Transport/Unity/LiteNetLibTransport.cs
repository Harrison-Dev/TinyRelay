using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using LiteNetLib;
using LiteNetLib.Utils;
using Unity.Netcode;
using UnityEngine;
using RandomNetKit.Core.Shared;

namespace RandomNetKit.Transport.Unity
{
    public class LiteNetLibTransport : NetworkTransport, INetEventListener
    {
        enum HostType
        {
            None,
            Server,
            Client
        }

        [Header("Relay Settings")]
        [Tooltip("Relay Server IP")]
        public string Address = "127.0.0.1";

        [Tooltip("Relay Server Port")]
        public ushort Port = 9050;

        [Tooltip("Base Key (会与 I_AM_HOST / I_AM_CLIENT 组合使用)")]
        public string BaseKey = "relay";

        [Header("LiteNetLib Config")]
        [Tooltip("Ping Interval (sec)")]
        public float PingInterval = 1f;

        [Tooltip("Disconnect Timeout (sec)")]
        public float DisconnectTimeout = 5f;

        [Tooltip("Delay between connection attempts (sec)")]
        public float ReconnectDelay = 0.5f;

        [Tooltip("Max connection attempts")]
        public int MaxConnectAttempts = 10;

        [Tooltip("Message Buffer Size (bytes)")]
        public int MessageBufferSize = 4096;

        public override ulong ServerClientId => 0;

        private NetManager m_NetManager;
        private HostType m_HostType = HostType.None;
        private NetPeer m_relayPeer;
        private ulong m_localClientId;
        private byte[] m_MessageBuffer;

        void Update()
        {
            m_NetManager?.PollEvents();
        }

        public override bool IsSupported => Application.platform != RuntimePlatform.WebGLPlayer;

        public override void Initialize(NetworkManager networkManager = null)
        {
            m_MessageBuffer = new byte[MessageBufferSize];

            m_NetManager = new NetManager(this)
            {
                AutoRecycle = true,
                IPv6Enabled = false,
                PingInterval = SecondsToMilliseconds(PingInterval),
                DisconnectTimeout = SecondsToMilliseconds(DisconnectTimeout),
                ReconnectDelay = SecondsToMilliseconds(ReconnectDelay),
                MaxConnectAttempts = MaxConnectAttempts
            };

            Debug.Log("[LiteNetLibTransport] Initialized (Relay version).");
        }

        public override bool StartServer()
        {
            if (m_HostType != HostType.None)
            {
                throw new InvalidOperationException($"Already started as {m_HostType}");
            }
            m_HostType = HostType.Server;

            bool success = m_NetManager.Start();
            if (!success)
            {
                Debug.LogError("[LiteNetLibTransport] Failed to Start() NetManager!");
                return false;
            }

            string fullKey = BaseKey + "|I_AM_HOST";
            Debug.Log($"[LiteNetLibTransport] StartServer -> Connect to Relay: {Address}:{Port}, key={fullKey}");
            var peer = m_NetManager.Connect(Address, Port, fullKey);

            if (peer == null)
            {
                Debug.LogError("[LiteNetLibTransport] Host Connect returned null peer!");
                return false;
            }

            return true;
        }

        public override bool StartClient()
        {
            if (m_HostType != HostType.None)
            {
                throw new InvalidOperationException($"Already started as {m_HostType}");
            }
            m_HostType = HostType.Client;

            bool success = m_NetManager.Start();
            if (!success)
            {
                Debug.LogError("[LiteNetLibTransport] Failed to Start() NetManager!");
                return false;
            }

            string fullKey = BaseKey + "|I_AM_CLIENT";
            Debug.Log($"[LiteNetLibTransport] StartClient -> Connect to Relay: {Address}:{Port}, key={fullKey}");
            var peer = m_NetManager.Connect(Address, Port, fullKey);

            if (peer == null)
            {
                Debug.LogError("[LiteNetLibTransport] Client Connect returned null peer!");
                return false;
            }

            return true;
        }

        public override void Shutdown()
        {
            if (m_NetManager != null)
            {
                m_NetManager.Stop();
            }
            m_HostType = HostType.None;
            m_relayPeer = null;
            Debug.Log("[LiteNetLibTransport] Shutdown");
        }

        public override void Send(ulong clientId, ArraySegment<byte> data, NetworkDelivery qos)
        {
            if (m_relayPeer == null || m_relayPeer.ConnectionState != ConnectionState.Connected)
            {
                Debug.LogWarning("[LiteNetLibTransport] Cannot send data. Relay peer is not connected.");
                return;
            }

            using var packet = new Packet(data, PacketType.Data);
            packet.Role = (m_HostType == HostType.Server) ? Role.Host : Role.Client;
            packet.SenderId = m_localClientId;
            packet.RecipientId = clientId;

            packet.Send(m_relayPeer, ConvertDelivery(qos));
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
            // TODO: Implement if needed
        }

        public override void DisconnectLocalClient()
        {
            m_NetManager.DisconnectAll();
            m_NetManager.Stop();
            m_HostType = HostType.None;
            m_relayPeer?.Disconnect();
            m_relayPeer = null;
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            return (m_relayPeer != null) ? (ulong)m_relayPeer.Ping * 2 : 0;
        }

        public void OnPeerConnected(NetPeer peer)
        {
            Debug.Log($"[LiteNetLibTransport] OnPeerConnected: peerId={peer.Id}, no assigned NGO ID yet.");
            m_relayPeer = peer;
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (m_relayPeer == peer)
            {
                Debug.LogWarning($"Disconnected from Relay: {disconnectInfo.Reason}");
                m_relayPeer = null;
                Shutdown();
                return;
            }
            else
            {
                Debug.LogError($"[LiteNetLibTransport] OnPeerDisconnected: peerId={peer.Id}, reason={disconnectInfo.Reason}");
            }
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Debug.LogWarning($"[LiteNetLibTransport] NetworkError: {socketError} @ {endPoint}");
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            using var packet = Packet.Deserialize(reader);
            float currentTime = Time.time;

            switch (packet.Type)
            {
                case PacketType.IdAssign:
                {
                    ulong assignedId = packet.RecipientId;
                    var clientId = assignedId;
                    Debug.Log($"[LiteNetLibTransport] Received assignedId={assignedId} from Relay. clientId={clientId}");
                    m_localClientId = clientId;

                    if (m_HostType == HostType.Client)
                    {
                        InvokeOnTransportEvent(NetworkEvent.Connect, packet.SenderId, default, currentTime);
                    }
                }
                break;
                case PacketType.Data:
                {
                    InvokeOnTransportEvent(NetworkEvent.Data, packet.SenderId, new ArraySegment<byte>(packet.Data.ToArray()), currentTime);
                }
                break;
                case PacketType.NotifyJoin:
                {
                    ulong joinedId = packet.SenderId;
                    InvokeOnTransportEvent(NetworkEvent.Connect, joinedId, default, currentTime);
                    NetworkLog.LogInfo($"[LiteNetLibTransport] NotifyJoin from ID={joinedId} Connected to NGO @ {m_localClientId}");
                }
                break;
            }

            reader.Recycle();
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
            request.Accept();
        }

        private DeliveryMethod ConvertDelivery(NetworkDelivery type)
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

        public void SetConnectionData(string ipAddress, ushort port)
        {
            Address = ipAddress;
            Port = port;
        }
    }
}
