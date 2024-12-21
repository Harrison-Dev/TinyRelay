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

        // Public Methods
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

        // NetEventListener Callbacks
        public void OnConnectionRequest(ConnectionRequest request)
        {
            // 接收端只要 key 正確，就接受連線；否則拒絕
            if (request.Data != null && request.Data.AvailableBytes > 0)
            {
                string receivedKey = request.Data.GetString();
                Console.WriteLine($"Received key: {receivedKey}");

                if (receivedKey == RelayKey && _netManager.ConnectedPeerList.Count < MaxConnections)
                {
                    // 接受這個連線
                    request.AcceptIfKey(RelayKey);
                    Console.WriteLine($"Connection accepted: {receivedKey}");
                }
                else
                {
                    Console.WriteLine($"Connection rejected: key mismatch or max limit. Received = {receivedKey}");
                    request.Reject();
                }
            }
            else
            {
                Console.WriteLine("Connection rejected: no key provided.");
                request.Reject();
            }
        }

        public void OnPeerConnected(NetPeer peer)
        {
            _peers.Add(peer);
            Console.WriteLine($"[Peer Connected] PeerId={peer.Id}, EndPoint={peer.ToString()}");
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _peers.Remove(peer);
            Console.WriteLine($"[Peer Disconnected] PeerId={peer.Id}, Reason={disconnectInfo.Reason}");
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Console.WriteLine($"[NetworkError] {socketError} at {endPoint}");
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            // 1. 讀取封包
            int length = reader.AvailableBytes;
            byte[] data = new byte[length];
            reader.GetBytes(data, length);

            // 2. 轉發封包給所有其他 peers
            foreach (var otherPeer in _peers)
            {
                if (otherPeer != peer && otherPeer.ConnectionState == ConnectionState.Connected)
                {
                    otherPeer.Send(data, deliveryMethod);
                }
            }

            reader.Recycle();
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            // 不處理 unconnected message
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            // 可選：紀錄延遲
        }

        // Private Methods
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
