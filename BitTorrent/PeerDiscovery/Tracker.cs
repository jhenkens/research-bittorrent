using System;
using System.Net;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using MiscUtil.Conversion;

namespace BitTorrent.PeerDiscovery
{
    public enum TrackerEvent
    {
        Started,
        Paused,
        Stopped
    }

    public class Tracker
    {
        public event EventHandler<List<IPEndPoint>> PeerListUpdated;

        public string Address { get; private set; }

        private DateTimeOffset LastPeerRequest { get; set; } = DateTimeOffset.MinValue;
        private DateTimeOffset LastSuccesfulPeerRequest { get; set; } = DateTimeOffset.MinValue;

        private static long DefaultPeerRequestFailureBackoffInSeconds { get; } = 15;
        private TimeSpan PeerRequestFailureBackoff { get; set; } = TimeSpan.FromSeconds(DefaultPeerRequestFailureBackoffInSeconds);
        private TimeSpan PeerRequestInterval { get; set; } = TimeSpan.FromMinutes(30);

        private HttpWebRequest httpWebRequest;

        public Tracker(string address)
        {
            Address = address;
        }

        #region Announcing

        public async void Update(Torrent torrent, TrackerEvent ev, string id, int port)
        {
            // wait until after request interval has elapsed before asking for new peers
            if (!CanRequestPeerUpdate(ev))
            {
                return;
            }

            var url = $"{Address}?" +
                $"info_hash={torrent.UrlSafeStringInfohash}&" +
                $"peer_id={id}&" +
                $"port={port}&" +
                $"uploaded={torrent.Uploaded}&" +
                $"downloaded={torrent.Downloaded}&" +
                $"left={torrent.Left}&" +
                $"event={Enum.GetName(typeof(TrackerEvent), ev).ToLower()}&" +
                "compact=1";

            LastPeerRequest = DateTimeOffset.UtcNow;
            
            await RequestUpdate(url);
            
            LastSuccesfulPeerRequest = DateTimeOffset.UtcNow;
        }

        private bool CanRequestPeerUpdate(TrackerEvent ev)
        {
            if (ev != TrackerEvent.Started)
            {
                return true;
            }
            
            if (DateTimeOffset.UtcNow < LastSuccesfulPeerRequest.Add(PeerRequestInterval))
            {
                if (DateTimeOffset.UtcNow < LastPeerRequest.Add(PeerRequestFailureBackoff))
                {
                    return true;
                }
            }
            
            return false;
        }

        private async Task RequestUpdate(string url)
        {
            using (var client = new HttpClient())
            {
                using (var response = await client.GetAsync(url))
                {
                    await HandleResponse(response);
                }
            }
        }

        private async Task HandleResponse(HttpResponseMessage response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.WriteLine($"error reaching tracker {this}: {response.StatusCode} {response.ReasonPhrase}");
                return;
            }

            var data = await response.Content.ReadAsByteArrayAsync();

            if (!(BEncoding.Decode(data) is Dictionary<string, object> info))
            {
                Console.WriteLine("unable to decode tracker announce response");
                return;
            }

            var newInterval = (long) info["interval"];
            PeerRequestInterval = TimeSpan.FromSeconds(newInterval);
            PeerRequestFailureBackoff =
                TimeSpan.FromSeconds(Math.Max(newInterval, DefaultPeerRequestFailureBackoffInSeconds));
            
            var peerInfo = (byte[]) info["peers"];

            var peers = new List<IPEndPoint>();
            for (var i = 0; i < peerInfo.Length / 6; i++)
            {
                var offset = i * 6;
                var address = peerInfo[offset] + "." + peerInfo[offset + 1] + "." + peerInfo[offset + 2] + "." +
                              peerInfo[offset + 3];
                int port = EndianBitConverter.Big.ToChar(peerInfo, offset + 4);

                peers.Add(new IPEndPoint(IPAddress.Parse(address), port));
            }

            var handler = PeerListUpdated;
            if (handler != null)
                handler(this, peers);
        }

        public void ResetLastRequest()
        {
            LastPeerRequest = DateTime.MinValue;
        }

        #endregion

        #region Helper

        public override string ToString()
        {
            return string.Format("[Tracker: {0}]", Address);
        }

        #endregion
    }
}

