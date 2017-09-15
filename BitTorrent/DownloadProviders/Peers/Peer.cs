using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
using MiscUtil.Conversion;

namespace BitTorrent.DownloadProviders.Peers
{

    public enum MessageType
    {
        Unknown = -3,
        Handshake = -2,
        KeepAlive = -1,
        Choke = 0,
        Unchoke = 1,
        Interested = 2,
        NotInterested = 3,
        Have = 4,
        Bitfield = 5,
        Request = 6,
        Piece = 7,
        Cancel = 8,
        Port = 9,
    }

    public class Peer
    {
        #region Events

        public event EventHandler Disconnected;
        public event EventHandler StateChanged;
        public event EventHandler<PeerConnection.DataRequest> BlockRequested;
        public event EventHandler<PeerConnection.DataRequest> BlockCancelled;
        public event EventHandler<PeerConnection.DataPackage> BlockReceived;

        #endregion

        #region Properties

        public string LocalId { get; set; }
        
        public Torrent Torrent { get; private set; }
        
        private PeerConnection Connection { get; }
        public string Key => Connection.Key;
        
        public bool[] IsPieceDownloaded = new bool[0];
        public string PiecesDownloaded { get { return string.Join("", IsPieceDownloaded.Select(x => Convert.ToInt32((bool) x))); } }
        public int PiecesRequiredAvailable { get { return IsPieceDownloaded.Select((x, i) => x && !Torrent.IsPieceVerified[i]).Count(x => x); } }
        public int PiecesDownloadedCount { get { return IsPieceDownloaded.Count(x => x); } }
        public bool IsCompleted => PiecesDownloadedCount == Torrent.PieceCount;

        public bool IsDisconnected;

        public bool IsHandshakeSent;
        public bool IsPositionSent;
        public bool IsChokeSent = true;
        public bool IsInterestedSent = false;

        public bool IsHandshakeReceived;
        public bool IsChokeReceived = true;
        public bool IsInterestedReceived = false;

        public bool[][] IsBlockRequested = new bool[0][];
        public int BlocksRequested { get { return IsBlockRequested.Sum(x => x.Count(y => y)); } }

        public DateTime LastActive;
        public DateTime LastKeepAlive = DateTime.MinValue;

        public long Uploaded;
        public long Downloaded;

        #endregion

        #region Constructors

        public Peer(Torrent torrent, string localId, TcpClient client) : 
            this(torrent, localId)
        {
            Connection = new PeerConnection(client, torrent.Infohash, localId, torrent.PieceCount);
        }

        public Peer(Torrent torrent, string localId, IPEndPoint endPoint) :
            this(torrent, localId)
        {
            Connection = new PeerConnection(endPoint, torrent.Infohash, localId, torrent.PieceCount);
        }

        private Peer(Torrent torrent, string localId)
        {
            LocalId = localId;
            Torrent = torrent;


            LastActive = DateTime.UtcNow;
            IsPieceDownloaded = new bool[Torrent.PieceCount];
            IsBlockRequested = new bool[Torrent.PieceCount][];
            for (var i = 0; i < Torrent.PieceCount; i++)
            {
                IsBlockRequested[i] = new bool[Torrent.GetBlockCount(i)];
            }
        }

        #endregion


        public bool TryConnect()
        {
            var connected = Connection.Connect().Result;
            if (!connected) return false;
            this.SendBitfield().Wait();
            return true;
        }

        public void Start()
        {
            Task.Run(RunLoop);
        }

        private async Task RunLoop()
        {
            while (!IsDisconnected)
            {
                var message = await Connection.ReadMessage();
                HandleMessage(message);
            }
        }

        private void ClientDisconnect(object sender, EventArgs args)
        {
            this.Disconnect();
        }

        public void Disconnect()
        {
            if (!IsDisconnected)
            {
                IsDisconnected = true;
                Log.WriteLine(this, "disconnected, down " + Downloaded + ", up " + Uploaded);
            }

            Connection.Disconnected -= ClientDisconnect;
            Connection.Disconnect();

            Disconnected?.Invoke(this, new EventArgs());
        }
        

        #region Incoming Messages

        private void HandleMessage(PeerConnection.Message message)
        {
            if (IsDisconnected) return;
            LastActive = DateTime.UtcNow;
            if (message != null)
            {
                switch (message.MessageType)
                {
                    case MessageType.Unknown:
                    case MessageType.Handshake:
                        return;
                    case MessageType.KeepAlive:
                        HandleKeepAlive();
                        return;
                    case MessageType.Choke:
                        HandleChoke();
                        return;
                    case MessageType.Unchoke:
                        HandleUnchoke();
                        return;
                    case MessageType.Interested:
                        HandleInterested();
                        return;
                    case MessageType.NotInterested:
                        HandleNotInterested();
                        return;
                    case MessageType.Have:
                        HandleHave(message);
                        return;
                    case MessageType.Bitfield:
                        HandleBitfield(message);
                        return;
                    case MessageType.Request:
                        HandleRequest(message);
                        return;
                    case MessageType.Piece:
                        HandlePiece(message);
                        return;
                    case MessageType.Cancel:
                        HandleCancel(message);
                        return;
                }
            }

            Log.WriteLine(this, $" Unhandled incoming message {message?.MessageType.ToString()}");
            Disconnect();
        }

        private void HandleKeepAlive() 
        {
            Log.WriteLine(this, "<- keep alive");
        }

        private void HandleChoke() 
        {
            Log.WriteLine(this, "<- choke");
            IsChokeReceived = true;

            var handler = StateChanged;
            handler?.Invoke(this, new EventArgs());
        }

        private void HandleUnchoke() 
        {
            Log.WriteLine(this, "<- unchoke");
            IsChokeReceived = false;

            var handler = StateChanged;
            handler?.Invoke(this, new EventArgs());
        }

        private void HandleInterested() 
        {
            Log.WriteLine(this, "<- interested");
            IsInterestedReceived = true;

            var handler = StateChanged;
            handler?.Invoke(this, new EventArgs());
        }

        private void HandleNotInterested() 
        {
            Log.WriteLine(this, "<- not interested");
            IsInterestedReceived = false;

            var handler = StateChanged;
            handler?.Invoke(this, new EventArgs());
        }

        private void HandleHave(PeerConnection.Message message)
        {
            var index = (int) message.Content;
            IsPieceDownloaded[index] = true;
            Log.WriteLine(this, "<- have " + index + " - " + PiecesDownloadedCount + " available (" + PiecesDownloaded + ")");

            var handler = StateChanged;
            handler?.Invoke(this, new EventArgs());
        }

        private void HandleBitfield(PeerConnection.Message message)
        {
            var isPieceDownloaded = (bool[]) message.Content;
            for (var i = 0; i < Torrent.PieceCount; i++)
                IsPieceDownloaded[i] = IsPieceDownloaded[i] || isPieceDownloaded[i];

            Log.WriteLine(this, "<- bitfield " + PiecesDownloadedCount + " available (" + PiecesDownloaded + ")");

            var handler = StateChanged;
            handler?.Invoke(this, new EventArgs());
        }

        private void HandleRequest(PeerConnection.Message message)
        {
            var request = (PeerConnection.DataRequest) message.Content;
            Log.WriteLine(this, $"<- request {request.Piece}, {request.Begin}, {request.Length}");
            request.Peer = this;
            
            var handler = BlockRequested;
            handler?.Invoke(this, request);
        }

        private void HandlePiece(PeerConnection.Message message)
        {
            var piece = (PeerConnection.DataPackage) message.Content;
            Log.WriteLine(this, $"<- piece {piece.Piece}, {piece.Begin}, {piece.Data.Length}");
            Downloaded += piece.Data.Length;

            piece.Peer = this;
            piece.Block = piece.Begin / Torrent.BlockSize;
            
            var handler = BlockReceived;
            handler?.Invoke(this, piece);
        }

        private void HandleCancel(PeerConnection.Message message )
        {
            var request = (PeerConnection.DataRequest) message.Content;
            request.Peer = this;
            Log.WriteLine(this, " <- cancel");

            var handler = BlockCancelled;
            handler?.Invoke(this,request);
        }

        #endregion

        #region Outgoing Messages

        public async Task SendKeepAlive()
        {
            if( LastKeepAlive > DateTime.UtcNow.AddSeconds(-30) )
                return;

            Log.WriteLine(this, "-> keep alive" );
            await Connection.SendKeepAlive();
            LastKeepAlive = DateTime.UtcNow;
        }

        public async Task SendChoke() 
        {
            if (IsChokeSent)
                return;
            
            Log.WriteLine(this, "-> choke" );
            await Connection.SendChoke();
            IsChokeSent = true;
        }

        public async Task SendUnchoke() 
        {
            if (!IsChokeSent)
                return;
            
            Log.WriteLine(this, "-> unchoke" );
            await Connection.SendUnchoke();
            IsChokeSent = false;
        }

        public async Task SendInterested()
        {
            if (IsInterestedSent)
                return;
            
            Log.WriteLine(this, "-> interested");
            await Connection.SendInterested();
            IsInterestedSent = true;
        }

        public async Task SendNotInterested() 
        {
            if (!IsInterestedSent)
                return;

            Log.WriteLine(this, "-> not interested");
            await Connection.SendNotInterested();
            IsInterestedSent = false;
        }

        public async Task SendHave(int index) 
        {
            Log.WriteLine(this, "-> have " + index);
            await Connection.SendHave(index);
        }

        public async Task SendBitfield() 
        {
            var isPieceDownloaded = this.Torrent.IsPieceVerified;
            Log.WriteLine(this, "-> bitfield " + String.Join("", isPieceDownloaded.Select(x => x ? 1 : 0)));
            await Connection.SendBitfield(isPieceDownloaded);
        }

        public async Task SendRequest(int index, int begin, int length) 
        {
            Log.WriteLine(this, "-> request " + index + ", " + begin + ", " + length);
            await Connection.SendRequest(index, begin, length);
        }

        public async Task SendPiece(int index, int begin, byte[] data) 
        {
            Log.WriteLine(this, "-> piece " + index + ", " + begin + ", " + data.Length);
            await Connection.SendPiece(index, begin, data);
            Uploaded += data.Length;
        }

        public async Task SendCancel(int index, int begin, int length) 
        {
            Log.WriteLine(this, "-> cancel");
            await Connection.SendCancel(index, begin, length);
        }

        #endregion

        

        #region Helper

        public override string ToString()
        {
            return string.Format("[{0} ({1})]", Connection?.IPEndPoint, Connection?.Id);
        }

        #endregion
    }
}