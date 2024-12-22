using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using Shared;

namespace TinyRelay
{
    class Program
    {
        static void Main(string[] args)
        {
            int port = 9050;
            Console.WriteLine("Starting TinyRelay on port " + port);
            var server = new RelayServer();
            if (!server.Start(port))
            {
                Console.WriteLine("Failed to start the relay server.");
                return;
            }

            Console.WriteLine("RelayServer started on port " + port + ".");
            Console.WriteLine("Press Enter to stop...");
            Console.ReadLine();

            server.Stop();
            Console.WriteLine("Server stopped.");
        }
    }

    public class RelayServer : INetEventListener
    {
        private NetManager _netManager;
        private Thread _pollThread;
        private bool _running;

        private const string KEY_PREFIX = "relay|";
        private const int MaxConnections = 8;

        // Host 的 Peer
        private NetPeer hostPeer = null;
        private const ulong hostStartId = 0;
        private const ulong clientStartId = 1;

        // 為 Client 分配 ID 時，要從 1 開始遞增
        private ulong nextClientId = clientStartId;

        // 用來儲存所有 Peer，以便之後做轉發
        private readonly Dictionary<NetPeer, ulong> _peerToIdMap = new Dictionary<NetPeer, ulong>();
        private readonly Dictionary<ulong, NetPeer> _idToPeerMap = new Dictionary<ulong, NetPeer>();

        public bool Start(int port)
        {
            _netManager = new NetManager(this)
            {
                AutoRecycle = true,
            };

            if (!_netManager.Start(port))
            {
                Console.WriteLine("[RelayServer] Failed to start NetManager.");
                return false;
            }

            _running = true;
            _pollThread = new Thread(PollLoop);
            _pollThread.Start();

            return true;
        }

        public void Stop()
        {
            _running = false;
            _netManager.Stop();
            if (_pollThread != null && _pollThread.IsAlive)
            {
                _pollThread.Join();
            }
        }

        private void PollLoop()
        {
            while (_running)
            {
                _netManager.PollEvents();
                Thread.Sleep(15);
            }
        }

        // -------------------- INetEventListener --------------------
        public void OnConnectionRequest(ConnectionRequest request)
        {
            /*
             * 我們約定：
             * Key = "relay|I_AM_HOST" 或 "relay|I_AM_CLIENT"
             */
            var dataReader = request.Data;
            if (dataReader != null && dataReader.AvailableBytes > 0)
            {
                string fullKey = dataReader.GetString(); // 例如 "relay|I_AM_HOST"
                Console.WriteLine($"[ConnectionRequest] Received key: {fullKey}");

                // Key check
                if (!fullKey.StartsWith(KEY_PREFIX))
                {
                    Console.WriteLine("[ConnectionRequest] Key prefix not match. Rejected.");
                    request.Reject();
                    return;
                }

                // Role check
                // 解析 "I_AM_HOST" or "I_AM_CLIENT"
                string role = fullKey.Substring(KEY_PREFIX.Length);

                // 有無超過最大連線
                if (_netManager.ConnectedPeerList.Count >= MaxConnections)
                {
                    Console.WriteLine("[ConnectionRequest] Rejected. Max connections reached.");
                    request.Reject();
                    return;
                }

                // 規則：
                // 1) 若尚未有 hostPeer，只有 "I_AM_HOST" 可以被接受，其餘拒絕
                // 2) 若已有 hostPeer，只有 "I_AM_CLIENT" 可以被接受，"I_AM_HOST" 拒絕
                if (hostPeer == null)
                {
                    // 還沒有 Host
                    if (role == "I_AM_HOST")
                    {
                        request.Accept();
                        Console.WriteLine("[ConnectionRequest] Accepting new Host.");
                    }
                    else
                    {
                        // 想當 client, 但還沒host => 拒絕
                        Console.WriteLine("[ConnectionRequest] No host yet, reject client.");
                        request.Reject();
                    }
                }
                else
                {
                    // 已有 Host
                    if (role == "I_AM_CLIENT")
                    {
                        request.Accept();
                        Console.WriteLine("[ConnectionRequest] Accepting new Client.");
                    }
                    else
                    {
                        // 又來一個 Host => 拒絕
                        Console.WriteLine("[ConnectionRequest] Already have host, reject new Host.");
                        request.Reject();
                    }
                }
            }
            else
            {
                Console.WriteLine("[ConnectionRequest] No data provided, reject.");
                request.Reject();
            }
        }

        public void OnPeerConnected(NetPeer peer)
        {
            Console.WriteLine($"[PeerConnected] {peer.ToString()}, PeerId={peer.Id}");

            // 判斷是 Host 還是 Client 並分配 ID
            if (hostPeer == null)
            {
                // 這個就是 Host
                hostPeer = peer;
                _peerToIdMap[peer] = hostStartId;
                _idToPeerMap[hostStartId] = peer;
                Console.WriteLine($"[PeerConnected] => Mark as Host (netId={hostStartId})");
                SendIdAssignment(peer, hostStartId, isHost: true);
            }
            else
            {
                // 這是 Client
                var assignedId = nextClientId++;
                _peerToIdMap[peer] = assignedId;
                _idToPeerMap[assignedId] = peer;
                Console.WriteLine($"[PeerConnected] => Mark as Client (netId={assignedId})");
                // 回傳一包資料給它： "ID=assignedId"
                SendIdAssignment(peer, assignedId, isHost: false);
            }
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Console.WriteLine($"[PeerDisconnected] {peer.ToString()}, Reason={disconnectInfo.Reason}");

            if (_peerToIdMap.TryGetValue(peer, out var oldId))
            {
                _peerToIdMap.Remove(peer);
                _idToPeerMap.Remove(oldId);

                // 如果這個 peer 是 Host
                if (hostPeer == peer)
                {
                    hostPeer = null;
                    // Reset client ID counter
                    nextClientId = clientStartId;
                    Console.WriteLine(
                        $"[PeerDisconnected] Host is gone. Now no host, client ID counter reset to {clientStartId}.");
                }
                else
                {
                    Console.WriteLine($"[PeerDisconnected] Client {oldId} is gone.");
                }
            }
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Console.WriteLine($"[NetworkError] {socketError} @ {endPoint}");
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber,
            DeliveryMethod deliveryMethod)
        {
            try
            {
                using Packet receivedPacket = Packet.Deserialize(reader);
                // 轉發給目標 Peer
                if (receivedPacket.RecipientId == ulong.MaxValue)
                {
                    // broadcast
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
                        Console.WriteLine(
                            $"[NetworkReceive] Recipient not found: {receivedPacket.RecipientId}. Discard.");
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

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader,
            UnconnectedMessageType messageType)
        {
            // ignore
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            /* ignore */
        }

        // ---------------------------------------------------
        // 給 Peer 發送第一包: 通知它的 netId
        private void SendIdAssignment(NetPeer peer, ulong assignedId, bool isHost)
        {
            using var assignIdPacket = new Packet(PacketType.IdAssign)
            {
                Role = Role.Relay,
                SenderId = hostStartId,
                RecipientId = assignedId
            };

            // 必須要送到的 走reliable
            // id assign 需要broadcast
            assignIdPacket.Send(peer, DeliveryMethod.ReliableUnordered);

            // 通知其他人
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
}