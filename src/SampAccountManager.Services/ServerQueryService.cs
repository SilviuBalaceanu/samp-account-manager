using System.Net;
using System.Net.Sockets;
using System.Text;
using SampAccountManager.Models;

namespace SampAccountManager.Services;

/// <summary>Interogare standard SA-MP ("SAMP"+ip+port+'i') pentru statusul real al serverului.</summary>
public static class ServerQueryService
{
    public static async Task<bool> RefreshAsync(ServerEntry server, CancellationToken ct = default)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(server.Host, ct);
            var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? throw new SocketException((int)SocketError.HostNotFound);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var q = new List<byte>(11) { (byte)'S', (byte)'A', (byte)'M', (byte)'P' };
            q.AddRange(ip.GetAddressBytes());
            q.Add((byte)(server.Port & 0xFF));
            q.Add((byte)(server.Port >> 8));
            q.Add((byte)'i');
            await udp.SendAsync(q.ToArray(), q.Count, new IPEndPoint(ip, server.Port));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var res = await udp.ReceiveAsync(cts.Token);
            var b = res.Buffer;
            // raspuns: SAMP(4) ip(4) port(2) 'i'(1) password(1) players(2) max(2) hostnameLen(4) hostname...
            if (b.Length < 15 || b[0] != 'S' || b[10] != (byte)'i') throw new InvalidDataException("raspuns invalid");
            server.Players = BitConverter.ToUInt16(b, 12);
            server.MaxPlayers = BitConverter.ToUInt16(b, 14);
            if (b.Length >= 20)
            {
                var len = (int)BitConverter.ToUInt32(b, 16);
                if (len >= 0 && 20 + len <= b.Length)
                    server.ReportedHostName = Encoding.Latin1.GetString(b, 20, len);
            }
            server.Status = ServerStatus.Online;
            server.CheckedAtUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            server.Status = ServerStatus.Offline;
            server.CheckedAtUtc = DateTime.UtcNow;
            return false;
        }
    }
}
