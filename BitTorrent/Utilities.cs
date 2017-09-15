using System.Linq;
using System.Threading.Tasks;

namespace BitTorrent
{
    public static class Utilities
    {
        public static string InfoHashAsHexString(byte[] infoHash)
        {
            return string.Join("", infoHash.Select(x => x.ToString("x2")));
        }

        //https://stackoverflow.com/a/24412022    
        public static async Task<byte[]> ReadExactlyAsync(this System.IO.Stream stream, int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer, offset, count - offset);
                if (read == 0)
                    throw new System.IO.EndOfStreamException();
                offset += read;
            }
            System.Diagnostics.Debug.Assert(offset == count);
            return buffer;
        }
    }
}