using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MiscUtil.Conversion;

namespace BitTorrent.DownloadProviders.Peers
{
    public class PeerConnection
    {
        public class DataRequest
        {
            public Peer Peer;
            public int Piece;
            public int Begin;
            public int Length;
            public bool IsCancelled;
        }

        public class DataPackage
        {
            public Peer Peer;
            public int Piece;
            public int Block;
            public int Begin;
            public byte[] Data;
        }

        public class Message
        {
            public MessageType MessageType { get; }
            public object Content { get; }

            private Message(MessageType messageType, object content)
            {
                MessageType = messageType;
                Content = content;
            }

            public static readonly Message KeepAlive = new Message(MessageType.KeepAlive, null);
            public static readonly Message Choke = new Message(MessageType.Choke, null);
            public static readonly Message Unchoke = new Message(MessageType.Unchoke, null);
            public static readonly Message Interested = new Message(MessageType.Interested, null);
            public static readonly Message NotInterested = new Message(MessageType.NotInterested, null);
            public static readonly Message Port = new Message(MessageType.Port, null);

            public static Message DecodeHave(byte[] bytes)
            {
                return new Message(MessageType.Have, EndianBitConverter.Big.ToInt32(bytes, 5));
            }
            
            public static Message DecodeBitfield(byte[] bytes, long pieces)
            {
                var isPieceDownloaded = new bool[pieces];

                var bitfield = new BitArray(bytes);

                //The first 4 bits will be the type, but we read backwards anyways.
                for (var i = 0; i < pieces; i++)
                {
                    isPieceDownloaded[i] = bitfield[bitfield.Length - 1 - i];
                }

                return new Message(MessageType.Bitfield,isPieceDownloaded);
            }
            
            public static Message DecodeRequest(byte[] bytes)
            {
                DecodeIndexes(bytes, out var index, out var begin);
                DecodeLengthInt(bytes, out var length);
                return new Message(
                    MessageType.Request,
                    new DataRequest
                    {
                        Piece = index,
                        Length = length,
                        Begin = begin
                    }
                );
            }

            public static Message DecodePiece(byte[] bytes)
            {
                var length = bytes.Length - 9;
                var index = EndianBitConverter.Big.ToInt32(bytes, 1);
                var begin = EndianBitConverter.Big.ToInt32(bytes, 5);

                var data = new byte[length];
                Buffer.BlockCopy(bytes, 9, data, 0, length);
                return new Message(MessageType.Request,
                    new DataPackage
                {
                    Piece = index,
                    Begin = begin,
                    Data = data
                });
            }
            
            public static Message DecodeCancel(byte[] bytes)
            {
                DecodeIndexes(bytes, out var index, out var begin);
                DecodeLengthInt(bytes, out var length);
                return new Message(
                    MessageType.Cancel,
                    new DataRequest
                    {
                        Piece = index,
                        Length = length,
                        Begin = begin
                    }
                );
            }

            private static void DecodeIndexes(byte[] bytes, out int index, out int begin)
            {
                index = EndianBitConverter.Big.ToInt32(bytes, 1);
                begin = EndianBitConverter.Big.ToInt32(bytes, 5);
            }

            private static void DecodeLengthInt(byte[] bytes, out int length)
            {
                length = EndianBitConverter.Big.ToInt32(bytes, 9);
            }


        }
        
        #region Properties
        public IPEndPoint IPEndPoint { get; }
        public byte[] InfoHash { get; }
        public string LocalId { get; }
        public long NumberOfPieces { get; }
        public long BitfieldLength { get; }
        
        public string Key => IPEndPoint.ToString();

        private TcpClient TcpClient { get; set; }
        private NetworkStream stream { get; set; }
        private const int HandshakeMessageLength = 68;
        private const string BittorrentProtocol = "BitTorrent protocol";
        

        private object LockObj = new object();
        
        public bool IsHandshakeSent;
        public bool IsHandshakeReceived;
        
        public bool IsConnected;

        public DateTimeOffset LastActive { get; private set; }

        public string Id { get; private set; }
        
        #endregion

        #region EventHandlers

        public event EventHandler Disconnected;

        #endregion

        #region Constructors

        public PeerConnection(TcpClient client, byte[] infoHash, string localId, long numberOfPieces) :
            this((IPEndPoint) client.Client.RemoteEndPoint, infoHash, localId, numberOfPieces)
        {
            TcpClient = client;
        }

        public PeerConnection(IPEndPoint endPoint,byte[] infoHash, string localId, long numberOfPieces)
        {
            IPEndPoint = endPoint;
            InfoHash = infoHash;
            LocalId = localId;
            NumberOfPieces = numberOfPieces;
            
            BitfieldLength = Convert.ToInt32(Math.Ceiling(NumberOfPieces / 8.0));
        }

        #endregion

        #region Tcp

        public async Task<bool> Connect()
        {
            if (TcpClient != null) return true;
            
            TcpClient = new TcpClient();
            try
            {
                await TcpClient.ConnectAsync(IPEndPoint.Address, IPEndPoint.Port);
            }
            catch (Exception e)
            {
                Log.WriteLine($"Exception connection to peer: {IPEndPoint}: {e.Message}");
                Disconnect();
                return false;
            }

            Log.WriteLine(this, "connected");

            stream = TcpClient.GetStream();

            IsConnected = await PerformHandshake();
            return IsConnected;
        }

        public void Disconnect()
        {
            if (IsConnected)
            {
                IsConnected = false;
            }

            TcpClient?.Close();

            Disconnected?.Invoke(this, new EventArgs());
        }
        
        #endregion
        
        
        #region Handshake

        private async Task<bool> PerformHandshake()
        {
            CheckIfAlreadyHandshakeAndThrow();

            Log.WriteLine(this, "-> handshake");
            
            await SendBytes(EncodeHandshake(InfoHash, LocalId));
            
            var bytes = await this.ReceiveBytes(HandshakeMessageLength);

            if (!DecodeHandshake(bytes, out var hash, out var id)) return false;
            
            if (!InfoHash.SequenceEqual(hash))
            {
                Log.WriteLine(this,
                    "invalid handshake, incorrect torrent hash: " +
                    "expecting=" + Utilities.InfoHashAsHexString(InfoHash) +
                    ", received =" + Utilities.InfoHashAsHexString(hash)); 
                Disconnect();
                return false;
            }
            Id = id;
            IsHandshakeReceived = true;
            return true;
        }
        
        private void CheckIfAlreadyHandshakeAndThrow()
        {
            if (!IsHandshakeSent)
            {
                lock (LockObj)
                {
                    if (!IsHandshakeSent)
                    {
                        IsHandshakeSent = true;
                        return;
                    }
                }
            }
            throw new Exception("Cannot rehandshake");
        }
        
        #endregion
            
        #region Stream Handlings
        private async Task SendBytes(byte[] bytes)
        {
            try
            {
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch(Exception e)
            {
                Disconnect();
            }
        }
                    
        private async Task<byte[]> ReceiveBytes(int length)
        {
            try
            {
                return await stream.ReadExactlyAsync(length);
            }
            catch(Exception e)
            {
                Disconnect();
                throw;
            }
        }

        public async Task<Message> ReadMessage()
        {
            Message result;
            var length = await ReadMessageLength();
            if (length == 0)
            {
                result = Message.KeepAlive;
            }
            else
            {
                var bytes = await ReceiveBytes(length);
                result = DecodeMessage(bytes);
            }
            LastActive = DateTimeOffset.UtcNow;
            return result;
        }

        private async Task<int> ReadMessageLength()
        {
            var bytes = await ReceiveBytes(4);
            return EndianBitConverter.Big.ToInt32(bytes, 0);
        }
        #endregion

        #region Incoming Messages

        private static MessageType GetMessageType(byte type)
        {
            if (Enum.IsDefined(typeof(MessageType), type))
                return (MessageType) type;

            return MessageType.Unknown;
        }

        private Message DecodeMessage(byte[] bytes)
        {
            var type = GetMessageType(bytes[0]);
            
            if (CheckExpectedPayloadLengthForType(type, bytes.Length))
            {
                switch (type)
                {
                    case MessageType.Unknown:
                        return null;
                    case MessageType.Choke:
                        return Message.Choke;
                    case MessageType.Unchoke:
                        return Message.Unchoke;
                    case MessageType.Interested:
                        return Message.Interested;
                    case MessageType.NotInterested:
                        return Message.NotInterested;
                    case MessageType.Have:
                        return Message.DecodeHave(bytes);
                    case MessageType.Bitfield:
                        return Message.DecodeBitfield(bytes,BitfieldLength);
                    case MessageType.Request:
                        return Message.DecodeRequest(bytes);
                    case MessageType.Piece:
                        return Message.DecodePiece(bytes);
                    case MessageType.Cancel:
                        return Message.DecodeCancel(bytes);
                    case MessageType.Port:
                        Log.WriteLine(this, " <- port: " + String.Join("", bytes.Select(x => x.ToString("x2"))));
                        return Message.Port;
                }
            }

            Log.WriteLine(this, " Unhandled incoming message " + String.Join("", bytes.Select(x => x.ToString("x2"))));
            Disconnect();
            return null;
        }

        #endregion

        #region Outgoing Messages


        public Task SendKeepAlive()
        {
            return SendBytes(EncodeKeepAlive());
        }

        public Task SendChoke() 
        {
            return SendBytes(EncodeChoke());
        }

        public Task SendUnchoke() 
        {
            return SendBytes(EncodeUnchoke());
        }

        public Task SendInterested()
        {
            return SendBytes(EncodeInterested());
        }

        public Task SendNotInterested() 
        {
            return SendBytes(EncodeNotInterested());
        }

        public Task SendHave(int index) 
        {
            return SendBytes(EncodeHave(index));
        }

        public Task SendBitfield(bool[] isPieceDownloaded) 
        {
            return SendBytes(EncodeBitfield(isPieceDownloaded));
        }

        public Task SendRequest(int index, int begin, int length) 
        {
            return SendBytes(EncodeRequest(index, begin, length));
        }

        public Task SendPiece(int index, int begin, byte[] data) 
        {
            return SendBytes(EncodePiece(index, begin, data));
        }

        public Task SendCancel(int index, int begin, int length) 
        {
            return SendBytes(EncodeCancel(index, begin, length));
        }

        #endregion

        #region Encoding

        private static byte[] EncodeHandshake(byte[] hash, string id)
        {
            var message = new byte[HandshakeMessageLength];
            message[0] = 19;
            Buffer.BlockCopy(Encoding.UTF8.GetBytes(BittorrentProtocol), 0, message, 1, 19);
            Buffer.BlockCopy(hash,0, message, 28, 20);
            Buffer.BlockCopy(Encoding.UTF8.GetBytes(id), 0, message, 48, 20);

            return message;
        }

        private static byte[] _KeepAlive = EndianBitConverter.Big.GetBytes(0); 
        public static byte[] EncodeKeepAlive()
        {
            return _KeepAlive;
        }

        private static byte[] _Choke = EncodeState(MessageType.Choke);
        public static byte[] EncodeChoke()
        {
            return _Choke;
        }

        private static byte[] _Unchoke = EncodeState(MessageType.Unchoke);
        public static byte[] EncodeUnchoke()
        {
            return _Unchoke;
        }
        
        private static byte[] _Interested = EncodeState(MessageType.Interested);
        public static byte[] EncodeInterested()
        {
            return _Interested;
        }

        private static byte[] _NotInterested = EncodeState(MessageType.NotInterested);
        public static byte[] EncodeNotInterested()
        {
            return _NotInterested;
        }

        private static byte[] EncodeState(MessageType type) 
        {
            var message = new byte[5];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(1), 0, message, 0, 4);
            message[4] = (byte)type;
            return message;
        }

        public static byte[] EncodeHave(int index) 
        {            
            var message = new byte[9];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(5), 0, message, 0, 4);
            message[4] = (byte)MessageType.Have;
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(index), 0, message, 5, 4);

            return message;
        }

        public static byte[] EncodeBitfield(bool[] isPieceDownloaded) 
        {
            var numPieces = isPieceDownloaded.Length;
            var numBytes = Convert.ToInt32(Math.Ceiling(numPieces / 8.0));
            var numBits = numBytes * 8;

            var length = numBytes + 1;

            var message = new byte[length + 4];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(length), 0, message, 0, 4);
            message[4] = (byte)MessageType.Bitfield;

            var downloaded = new bool[numBits];
            for (var i = 0; i < numPieces; i++)
                downloaded[i] = isPieceDownloaded[i];

            var bitfield = new BitArray(downloaded);
            var reversed = new BitArray(numBits);
            for (var i = 0; i < numBits; i++)
                reversed[i] = bitfield[numBits - i - 1];

            reversed.CopyTo(message, 5);

            return message;
        }

        public static byte[] EncodeRequest(int index, int begin, int length) 
        {
            var message = new byte[17];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(13), 0, message, 0, 4);
            message[4] = (byte)MessageType.Request;
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(index), 0, message, 5, 4);
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(begin), 0, message, 9, 4);
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(length), 0, message, 13, 4);

            return message;
        }

        public static byte[] EncodePiece(int index, int begin, byte[] data) 
        {
            var length = data.Length + 9;

            var message = new byte[length + 4];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(length), 0, message, 0, 4);
            message[4] = (byte)MessageType.Piece;
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(index), 0, message, 5, 4);
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(begin), 0, message, 9, 4);
            Buffer.BlockCopy(data, 0, message, 13, data.Length);

            return message;
        }

        public static byte[] EncodeCancel(int index, int begin, int length) 
        {
            var message = new byte[17];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(13), 0, message, 0, 4);
            message[4] = (byte)MessageType.Cancel;
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(index), 0, message, 5, 4);
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(begin), 0, message, 9, 4);
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(length), 0, message, 13, 4);

            return message;
        }

        #endregion

        #region Decoding


        private static bool DecodeHandshake(byte[] bytes, out byte[] hash, out string id)
        {
            hash = null;
            id = null;

            if (bytes.Length != HandshakeMessageLength || bytes[0] != 19)
            {
                Log.WriteLine("invalid handshake, must be of length 68 and first byte must equal 19");
                return false;
            }

            if (Encoding.UTF8.GetString(bytes,1,19) != BittorrentProtocol)
            {
                Log.WriteLine("invalid handshake, protocol must equal \"BitTorrent protocol\"");
                return false;
            }

            // flags
            //byte[] flags = bytes.Skip(20).Take(8).ToArray();

            Buffer.BlockCopy(bytes, 28, hash, 0, 20);
            id = Encoding.UTF8.GetString(bytes, 48, 20);

            return true;
        }


        public bool CheckExpectedPayloadLengthForType(MessageType type, long payloadLength)
        {
            var expectedLength = 0l;
            var minLength = 0l;
            var shouldValidate = true;
            switch (type)
            {
                case MessageType.Choke:
                case MessageType.Unchoke:
                case MessageType.Interested:
                case MessageType.NotInterested:
                    expectedLength = 1;
                    break;
                case MessageType.Have:
                    expectedLength = 5;
                    break;
                case MessageType.Bitfield:
                    expectedLength = BitfieldLength + 1;
                    break;
                case MessageType.Request:
                case MessageType.Cancel:
                    expectedLength = 13;
                    break;
                case MessageType.Piece:
                    minLength = 9;
                    break;


                default:
                    shouldValidate = false;
                    break;
            }

            if (payloadLength < minLength)
            {
                Log.WriteLine($"invalid {Enum.GetName(typeof(MessageType), type)} paylod length");
                return false;
            }

            if (shouldValidate)
            {
                if (payloadLength != expectedLength)
                {
                    Log.WriteLine($"invalid {Enum.GetName(typeof(MessageType), type)} payload length");
                    return false;
                }
            }
            return true;
        }




        #endregion

        #region Helper

        public override string ToString()
        {
            return string.Format("[{0} ({1})]", IPEndPoint, Id);
        }

        #endregion
    }
}