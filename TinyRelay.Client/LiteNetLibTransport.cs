using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Unity.Netcode;
using UnityEngine;
using Shared;

namespace Netcode.Transports.LiteNetLib
{
    public class LiteNetLibTransport : NetworkTransport, INetEventListener
    {
        /// <summary>
        /// 標示本機是 Host or Client
        /// </summary>
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

        [Tooltip("Base Key (會與 I_AM_HOST / I_AM_CLIENT 組合使用)")]
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

        /// <summary>
        /// NGO 要求 Server = 0
        /// </summary>
        public override ulong ServerClientId => 0;

        // --------------------------------------------------------------------------------

        private NetManager m_NetManager;
        private HostType m_HostType = HostType.None;

        private NetPeer m_relayPeer;
        private ulong m_localClientId;

        /// <summary>
        /// 用來暫存封包資料再轉給 NGO
        /// </summary>
        private byte[] m_MessageBuffer;

        // --------------------------------------------------------------------------------
        // MonoBehaviour 週期 (在 Unity 中)
        void Update()
        {
            m_NetManager?.PollEvents();
        }

        // --------------------------------------------------------------------------------
        // NetworkTransport 規定
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

        /// <summary>
        /// Host 模式 => NGO 期望本機 = ServerClientId = 0。
        /// 這裡會連線到 Relay，並在收到 Relay 發回的「ID=0」後，才向 NGO 報告。
        /// </summary>
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

            // 用 "relay|I_AM_HOST" 當作 Key
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

        /// <summary>
        /// Client 模式 => NGO 期望 netId != 0；將在收到 Relay 回應後分配 1,2,3...
        /// </summary>
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

            // 準備封包：Role Byte, Sender ID, Recipient ID, Data
            using var packet = new Packet(data, PacketType.Data);
            packet.Role = (m_HostType == HostType.Server) ? Role.Host : Role.Client;
            packet.SenderId = m_localClientId;
            packet.RecipientId = clientId; // 0 表示所有（在轉發時會被中繼伺服器更新）

            packet.Send(m_relayPeer, ConvertDelivery(qos));
        }


        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            // 由於我們用 event-based 方式 OnNetworkReceive => InvokeOnTransportEvent
            // 這邊統一回傳 Nothing
            clientId = 0;
            payload = default;
            receiveTime = Time.realtimeSinceStartup;
            return NetworkEvent.Nothing;
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            // TODO : 要請relay disconnect特定client的rpc，目前先不做，因為目前的relay server沒有這個功能
            // NetPeer targetPeer = m_relayPeer;
            // if (targetPeer != null)
            // {
            //     targetPeer.Disconnect();
            // }
        }

        public override void DisconnectLocalClient()
        {
            m_NetManager.DisconnectAll();
            m_NetManager.Stop();
            m_HostType = HostType.None;
            m_relayPeer.Disconnect();
            m_relayPeer = null;
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            return (m_relayPeer != null) ? (ulong)m_relayPeer.Ping * 2 : 0;
        }

        // --------------------------------------------------------------------------------
        // INetEventListener 介面實作
        public void OnPeerConnected(NetPeer peer)
        {
            Debug.Log($"[LiteNetLibTransport] OnPeerConnected: peerId={peer.Id}, no assigned NGO ID yet.");
            m_relayPeer = peer;
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            // TODO : 要請relay disconnect特定peer，目前先不做，因為目前的relay server沒有這個功能
            if (m_relayPeer == peer)
            {
                Debug.LogWarning($"Disconnected from Relay: {disconnectInfo.Reason}");
                // Debug.Log($"[LiteNetLibTransport] OnPeerDisconnected: peerId={peer.Id}, reason={disconnectInfo.Reason}");
                m_relayPeer = null;
                Shutdown(); // Peer Only relay server
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
                    // 代表這是 Relay 告知我們 => "assignedId"
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
                    // 記錄接收時間
                    // 轉交給 NGO
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
            // Relay Server 已做檢查，我們本地不檢查 => 接受即可
            request.Accept();
        }

        // --------------------------------------------------------------------------------
        // Helpers
        private DeliveryMethod ConvertDelivery(NetworkDelivery type)
        {
            switch (type)
            {
                case NetworkDelivery.Unreliable:
                    return DeliveryMethod.Unreliable;
                case NetworkDelivery.UnreliableSequenced:
                    return DeliveryMethod.Sequenced;
                case NetworkDelivery.Reliable:
                    return DeliveryMethod.ReliableUnordered;
                case NetworkDelivery.ReliableSequenced:
                    return DeliveryMethod.ReliableOrdered;
                case NetworkDelivery.ReliableFragmentedSequenced:
                    return DeliveryMethod.ReliableOrdered;
            }
            return DeliveryMethod.ReliableOrdered;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ResizeMessageBuffer(int size)
        {
            m_MessageBuffer = new byte[size];
            Debug.LogWarning($"[LiteNetLibTransport] Resized message buffer to {size} bytes.");
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
