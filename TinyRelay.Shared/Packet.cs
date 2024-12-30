using LiteNetLib;
using LiteNetLib.Utils;
using System;

namespace TinyRelay.Shared
{
    public enum PacketType : byte
    {
        Connect = 0x01,
        Disconnect = 0x02,
        Data = 0x03,
        IdAssign = 0x04,
        NotifyJoin = 0x05,
        NotifyLeave = 0x06
    }

    public enum Role : byte
    {
        Host = 0x01,
        Client = 0x02,
        Relay = 0x03
    }

    public sealed class Packet : IDisposable
    {
        private bool _disposed;
        private byte[] _data;
        private bool _ownsData;

        public PacketType Type { get; set; }
        public Role Role { get; set; }
        public ulong SenderId { get; set; }
        public ulong RecipientId { get; set; }

        public ReadOnlySpan<byte> Data => _data ?? Array.Empty<byte>();

        public Packet(PacketType type = PacketType.Data)
        {
            Type = type;
            _ownsData = true;
        }

        public Packet(ReadOnlySpan<byte> data, PacketType type = PacketType.Data) : this(type)
        {
            SetData(data);
        }

        public Packet WithRole(Role role)
        {
            Role = role;
            return this;
        }

        public Packet WithSender(ulong senderId)
        {
            SenderId = senderId;
            return this;
        }

        public Packet WithRecipient(ulong recipientId)
        {
            RecipientId = recipientId;
            return this;
        }

        public Packet WithData(ReadOnlySpan<byte> data)
        {
            SetData(data);
            return this;
        }

        private void SetData(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                _data = null;
                return;
            }

            if (_data == null || _data.Length != data.Length)
            {
                _data = new byte[data.Length];
            }

            data.CopyTo(_data);
        }

        public void Serialize(NetDataWriter writer)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Packet));

            writer.Put((byte)Type);
            writer.Put((byte)Role);
            writer.Put(SenderId);
            writer.Put(RecipientId);

            var dataSpan = Data;
            writer.Put(dataSpan.Length);
            if (!dataSpan.IsEmpty)
            {
                writer.Put(dataSpan.ToArray());
            }
        }

        public NetDataWriter SerializeToWriter()
        {
            var writer = new NetDataWriter(true);
            Serialize(writer);
            return writer;
        }

        public void Send(NetPeer peer, DeliveryMethod method)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Packet));

            var writer = SerializeToWriter();
            peer.Send(writer, method);
        }

        public static Packet Deserialize(NetDataReader reader)
        {
            try
            {
                if (reader.AvailableBytes < 22)
                    throw new InvalidOperationException($"Not enough data to deserialize Packet. Available: {reader.AvailableBytes} bytes. Expected at least 22 bytes.");

                var type = (PacketType)reader.GetByte();
                var role = (Role)reader.GetByte();
                var senderId = reader.GetULong();
                var recipientId = reader.GetULong();

                if (reader.AvailableBytes < 4)
                    throw new InvalidOperationException("Not enough data to read DataLength.");

                int dataLength = reader.GetInt();
                if (dataLength < 0)
                    throw new InvalidOperationException($"Invalid DataLength: {dataLength}.");

                if (reader.AvailableBytes < dataLength)
                    throw new InvalidOperationException($"Not enough data to read the Data field. Required: {dataLength} bytes, Available: {reader.AvailableBytes} bytes.");

                byte[] data = null;
                if (dataLength > 0)
                {
                    data = new byte[dataLength];
                    reader.GetBytes(data, 0, dataLength);
                }

                return new Packet(type)
                    .WithRole(role)
                    .WithSender(senderId)
                    .WithRecipient(recipientId)
                    .WithData(data);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Deserialize] Failed to deserialize Packet: {ex.Message}");
                throw;
            }
        }

        public Packet Clone()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Packet));

            var clone = new Packet(Type)
            {
                Role = Role,
                SenderId = SenderId,
                RecipientId = RecipientId
            };

            if (_data != null)
            {
                clone._data = new byte[_data.Length];
                _data.CopyTo(clone._data, 0);
            }

            return clone;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_ownsData)
                {
                    _data = null;
                }
                _disposed = true;
            }
        }
    }
} 