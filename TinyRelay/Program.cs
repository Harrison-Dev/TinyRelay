using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;

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

            Console.WriteLine("SimpleRelayServer started on port " + port + ".");
            Console.WriteLine("SimpleRelayServer started. Press Enter to stop...");
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
        private const int MaxConnections = 100;

        // Host 的 Peer
        private NetPeer hostPeer = null;
        // 為 Client 分配 ID 時，要從 1 開始遞增
        private ulong nextClientId = 1;

        // 用來儲存所有 Peer，以便之後做轉發
        private readonly Dictionary<NetPeer, ulong> _peerToIdMap = new Dictionary<NetPeer, ulong>();

        public bool Start(int port)
        {
            _netManager = new NetManager(this)
            {
                AutoRecycle = true,
            };

            if (!_netManager.Start(port))
            {
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

            // 從連線時的key 來判斷是 Host or Client
            // netId 分配:
            //  - Host => 0
            //  - Client => nextClientId++

            // LiteNetLib 無內建直接看 "AcceptIfKey()" 後 key；我們可以在 ConnectionRequest 裡先存，或在後續封包裡。
            // 簡化處理：我們假設 "I_AM_HOST" or "I_AM_CLIENT" 還能從 peer.Tag 取得，但實際 LiteNetLib沒有直接提供
            // => 為簡便，可以在 OnConnectionRequest accept之前 用 request.Data.Put()。實務上可以用 NetPacket 先傳一次。
            // 這裡就示範: 假設 role = ...
            // -- 為了 Demo，我們再偵測 "role" 方式：借由對 NetPeer.Name, or request.Data(無法輕易再存取)...

            // 這裡為了簡化，我們用 "if hostPeer==null => 這就是Host" 否則 => Client
            if (hostPeer == null)
            {
                // 這個就是Host
                hostPeer = peer;
                _peerToIdMap[peer] = 0;
                Console.WriteLine($"[PeerConnected] => Mark as Host (netId=0)");
                // 回傳一包資料給它： "ID=0"
                SendIdAssignment(peer, 0);
            }
            else
            {
                // 這是 Client
                var assignedId = nextClientId++;
                _peerToIdMap[peer] = assignedId;
                Console.WriteLine($"[PeerConnected] => Mark as Client (netId={assignedId})");
                // 回傳一包資料給它： "ID=assignedId"
                SendIdAssignment(peer, assignedId);
            }
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Console.WriteLine($"[PeerDisconnected] {peer.ToString()}, Reason={disconnectInfo.Reason}");

            if (_peerToIdMap.TryGetValue(peer, out var oldId))
            {
                _peerToIdMap.Remove(peer);
                // 如果這個 peer 是 Host
                if (hostPeer == peer)
                {
                    hostPeer = null;
                    Console.WriteLine($"[PeerDisconnected] Host is gone. Now no host, new host can connect next.");
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

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            int length = reader.UserDataSize;
            byte[] data = new byte[length];
            reader.GetBytes(data, length);

            // 轉發給其他 peers
            foreach (var kvp in _peerToIdMap)
            {
                var otherPeer = kvp.Key;
                if (otherPeer != peer && otherPeer.ConnectionState == ConnectionState.Connected)
                {
                    otherPeer.Send(data, deliveryMethod);
                }
            }
            reader.Recycle();
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            // ignore
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { /* ignore */ }

        // ---------------------------------------------------
        // 給 Peer 發送第一包: 通知它的 netId
        private void SendIdAssignment(NetPeer peer, ulong assignedId)
        {
            // 傳一段資料，格式: [0xFF標記][8 bytes of assignedId]
            // 用最簡單的方式示範
            NetDataWriter writer = new NetDataWriter();
            writer.Put((byte)0xFF); // 自訂標記
            writer.Put(assignedId);
            peer.Send(writer, DeliveryMethod.ReliableUnordered);
            
            Console.WriteLine($"Sending id {assignedId} to peer {peer.ToString()}.");
        }
    }
}
