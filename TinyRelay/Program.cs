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
            // 1. 啟動 Relay Server
            int port = 9050;
            Console.WriteLine("Starting TinyRelay on port " + port);
            var server = new RelayServer();
            if (!server.Start(port))
            {
                Console.WriteLine("Failed to start the relay server.");
                return;
            }

            Console.WriteLine("Relay server started. Press Enter to stop...");
            Console.ReadLine();

            // 2. 停止 Relay
            server.Stop();
            Console.WriteLine("Relay server stopped.");
        }
    }

    public class RelayServer : INetEventListener
    {
        private NetManager _netManager;
        private Thread _pollThread;
        private bool _running;

        // 儲存所有連線 Peer
        private readonly List<NetPeer> _peers = new List<NetPeer>();

        // 設定: 連線 Key 和最大連線數
        private const string RelayKey = "relay";
        private const int MaxConnections = 100;

        public bool Start(int port)
        {
            _netManager = new NetManager(this)
            {
                AutoRecycle = true,
                // IPv6Mode = IPv6Mode.Disabled
            };

            bool success = _netManager.Start(port);
            if (!success)
                return false;

            _running = true;
            _pollThread = new Thread(PollLoop);
            _pollThread.Start();

            Console.WriteLine($"RelayServer started on port {port}.");
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

        // -------------------- INetEventListener --------------------
        public void OnConnectionRequest(ConnectionRequest request)
        {
            // Key 驗證
            if (request.Data != null && request.Data.AvailableBytes > 0)
            {
                string receivedKey = request.Data.GetString();
                Console.WriteLine($"[ConnectionRequest] Received key: {receivedKey}"); // ★ NEW LOG

                if (receivedKey == RelayKey && _netManager.ConnectedPeerList.Count < MaxConnections)
                {
                    request.AcceptIfKey(RelayKey);
                    Console.WriteLine($"[ConnectionRequest] Connection accepted: {receivedKey}"); // ★ NEW LOG
                }
                else
                {
                    Console.WriteLine($"[ConnectionRequest] Connection rejected: key mismatch or max limit. Received = {receivedKey}");
                    request.Reject();
                }
            }
            else
            {
                Console.WriteLine("[ConnectionRequest] Connection rejected: no key provided.");
                request.Reject();
            }
        }

        public void OnPeerConnected(NetPeer peer)
        {
            _peers.Add(peer);

            // ★ NEW LOG
            // 若想簡單猜「PeerId=0 可能是 Host」(視 Unity Transport 的分配策略)，可加上 (Host?) 提示
            // 但其實 Relay 不真正知道誰是 Host。
            var maybeHostMark = (peer.Id == 0) ? "(可能是Host?)" : "";
            Console.WriteLine($"[Peer Connected] PeerId={peer.Id}{maybeHostMark}, EndPoint={peer.ToString()}");
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _peers.Remove(peer);

            // ★ NEW LOG
            var maybeHostMark = (peer.Id == 0) ? "(可能是Host?)" : "";
            Console.WriteLine($"[Peer Disconnected] PeerId={peer.Id}{maybeHostMark}, Reason={disconnectInfo.Reason}, EndPoint={peer.ToString()}");
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Console.WriteLine($"[NetworkError] {socketError} at {endPoint}");
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            int length = reader.AvailableBytes;
            byte[] data = new byte[length];
            reader.GetBytes(data, length);

            // ★ NEW LOG
            // 1) 紀錄「誰」送來資料 (PeerId) + 端點
            // 2) 紀錄封包長度
            Console.WriteLine($"[OnNetworkReceive] Received {length} bytes from PeerId={peer.Id}, Endpoint={peer.ToString()}.");

            // 轉發給其他 peers
            foreach (var otherPeer in _peers)
            {
                if (otherPeer != peer && otherPeer.ConnectionState == ConnectionState.Connected)
                {
                    // ★ NEW LOG
                    Console.WriteLine($"[OnNetworkReceive] -> Forwarding {length} bytes from PeerId={peer.Id} to PeerId={otherPeer.Id}. Source={peer.ToString()}, Target={otherPeer.ToString()}");
                    otherPeer.Send(data, deliveryMethod);
                }
            }
            reader.Recycle();
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            // 不處理 unconnected message
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { /* optional */ }

        // -------------------- Private --------------------
        private void PollLoop()
        {
            while (_running)
            {
                _netManager.PollEvents();
                Thread.Sleep(15);
            }
        }
    }
}
