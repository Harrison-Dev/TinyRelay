using LiteNetLib;
using LiteNetLib.Utils;
using System;

namespace Shared
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

        // Default constructor for creating new packets
        public Packet(PacketType type = PacketType.Data)
        {
            Type = type;
            _ownsData = true;
        }

        // Constructor for creating data packets
        public Packet(ReadOnlySpan<byte> data, PacketType type = PacketType.Data) : this(type)
        {
            SetData(data);
        }

        // Fluent interface for setting properties
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

        // Helper method to set data efficiently
        private void SetData(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                _data = null;
                return;
            }

            if (_data == null || _data.Length != data.Length)
            {
                // Allocate a new array only if necessary
                _data = new byte[data.Length];
            }

            data.CopyTo(_data);
        }

        // Serialize the packet to the provided writer
        public void Serialize(NetDataWriter writer)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Packet));

            writer.Put((byte)Type);
            writer.Put((byte)Role);
            writer.Put(SenderId);
            writer.Put(RecipientId);

            var dataSpan = Data;
            writer.Put(dataSpan.Length); // Always write DataLength as 4 bytes
            if (!dataSpan.IsEmpty)
            {
                writer.Put(dataSpan.ToArray()); // Write data if present
            }

            // Logging: Log the size of the serialized data
            // Console.WriteLine($"[Serialize] Packet serialized with total length: {writer.Length} bytes.");
        }

        // Serialize to a new writer without pooling
        public NetDataWriter SerializeToWriter()
        {
            var writer = new NetDataWriter(true); // Initialize with encryption if needed
            Serialize(writer);
            return writer;
        }

        // Serialize and send the packet to the specified peer
        public void Send(NetPeer peer, DeliveryMethod method)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Packet));

            var writer = SerializeToWriter();

            // Logging: Log the data being sent
            // Console.WriteLine($"[Send] Sending Packet - Type: {Type}, Role: {Role}, SenderId: {SenderId}, RecipientId: {RecipientId}, DataLength: {Data.Length}, SerializedLength: {writer.Length} bytes.");

            peer.Send(writer, method);

            // No pooling; no need to recycle or dispose the writer
        }

        // Static factory method for deserialization with enhanced error handling
        public static Packet Deserialize(NetDataReader reader)
        {
            try
            {
                // Minimum bytes required: 1 (Type) + 1 (Role) + 8 (SenderId) + 8 (RecipientId) + 4 (DataLength) = 22 bytes
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

                // Logging: Log the details of the deserialized packet
                // Console.WriteLine($"[Deserialize] Packet deserialized - Type: {type}, Role: {role}, SenderId: {senderId}, RecipientId: {recipientId}, DataLength: {dataLength} bytes.");

                return new Packet(type)
                    .WithRole(role)
                    .WithSender(senderId)
                    .WithRecipient(recipientId)
                    .WithData(data);
            }
            catch (Exception ex)
            {
                // Log the exception details (adjust the logging mechanism as needed)
                Console.Error.WriteLine($"[Deserialize] Failed to deserialize Packet: {ex.Message}");
                throw;
            }
        }

        // Create a deep copy of the packet
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
                clone._ownsData = true;
            }

            return clone;
        }

        // Dispose pattern implementation
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
