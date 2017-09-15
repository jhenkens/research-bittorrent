using System;
using System.IO;
using System.Threading;
using BitTorrent;

namespace Program
{
    public static class Program
    {
        private static Client client;
        private static ManualResetEventSlim manualResetEventSlim = new ManualResetEventSlim();

        public static void Main(string[] args)
        {
            if (args.Length != 3 || !int.TryParse(args[0], out var port) || !File.Exists(args[1]))
            {
                Console.WriteLine("Error: requires port, torrent file and download directory as first, second and third arguments");
                return;
            }

            client = new Client(port, args[1], args[2]);
            client.Start();

            Console.CancelKeyPress += (x, y) => client.Stop();

            manualResetEventSlim.Wait();
        }

        private static void Stop()
        {
            client.Stop();
            manualResetEventSlim.Set();
        }
    }
}

