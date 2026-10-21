using System.Buffers.Binary;
using System.Text;
using GrowTool77.Aura;

namespace AuraTests;

internal sealed class Bin
{
    private readonly MemoryStream _ms = new();
    private readonly BinaryWriter _w;
    public Bin() => _w = new BinaryWriter(_ms);
    public Bin U8(int v)  { _w.Write((byte)v); return this; }
    public Bin U16(int v) { _w.Write((ushort)v); return this; }
    public Bin U32(uint v) { _w.Write(v); return this; }
    public Bin I32(int v) { _w.Write(v); return this; }
    public Bin F32(float v) { _w.Write(v); return this; }
    public Bin Bytes(byte[] b) { _w.Write(b); return this; }
    public Bin Str16(string s) { var b = Encoding.Latin1.GetBytes(s); _w.Write((ushort)b.Length); _w.Write(b); return this; }
    public Bin Str32(string s) { var b = Encoding.Latin1.GetBytes(s); _w.Write((uint)b.Length); _w.Write(b); return this; }
    public byte[] ToArray() { _w.Flush(); return _ms.ToArray(); }
}

internal static class Pkt
{
    public static byte[] Text(int type, string text)
    {
        var body = Encoding.Latin1.GetBytes(text);
        var p = new byte[4 + body.Length + 1];
        BinaryPrimitives.WriteInt32LittleEndian(p, type);
        Buffer.BlockCopy(body, 0, p, 4, body.Length);
        return p;
    }

    public static byte[] Game(byte type, int netId = -1, int itemNetId = 0, uint flags = 8,
                              float floatVar = 0, int intData = 0, float x = 0, float y = 0, byte[]? extra = null)
    {
        extra ??= Array.Empty<byte>();
        var p = new byte[4 + 56 + extra.Length];
        var s = p.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, 4);
        s[4] = type;
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], netId);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], itemNetId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], flags);
        BinaryPrimitives.WriteSingleLittleEndian(s[20..], floatVar);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], intData);
        BinaryPrimitives.WriteSingleLittleEndian(s[28..], x);
        BinaryPrimitives.WriteSingleLittleEndian(s[32..], y);
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], (uint)extra.Length);
        Buffer.BlockCopy(extra, 0, p, 60, extra.Length);
        return p;
    }

    public static byte[] OnSendToServer(int port, string address)
    {
        var v = new Bin().U8(7)
            .U8(0).U8(2).Str32("OnSendToServer")
            .U8(1).U8(9).I32(port)
            .U8(2).U8(9).I32(777)
            .U8(3).U8(9).I32(4242)
            .U8(4).U8(2).Str32(address)
            .U8(5).U8(5).U32(1)
            .U8(6).U8(2).Str32("tester")
            .ToArray();
        return Game(1, extra: v);
    }

    public static byte[] Variant(params string[] strings)
    {
        var b = new Bin().U8(strings.Length);
        for (int i = 0; i < strings.Length; i++) b.U8(i).U8(2).Str32(strings[i]);
        return Game(1, extra: b.ToArray());
    }
}

internal static class Worlds
{
    public const int W = 100, H = 60;

    // Synthetic map data following the documented format, with a mix of
    // tile-extra types. unknownAt >= 0 inserts an unknown extra type there.
    public static byte[] Build(int unknownAt = -1, int bigSigns = 40, bool withObjects = true)
    {
        var b = new Bin();
        b.U16(25).U32(0).Str16("AURATEST").U32(W).U32(H).U32(W * H).Bytes(new byte[5]);

        for (int i = 0; i < W * H; i++)
        {
            if (i == unknownAt) { Tile(b, 2, 1).U8(200); continue; }
            switch (i)
            {
                case 0: Tile(b, 6, 1).U8(1).Str16("EXIT").U8(0); break;                                   // door
                case 1: Tile(b, 242, 1).U8(3).U8(0).U32(12345).U32(2).U32(1).U32(2).U32(0).U32(0); break;  // lock
                case 2: Tile(b, 3, 1).U8(4).U32(100).U8(3); break;                                        // seed
                case 3: Tile(b, 20, 1).U8(2).Str16("Hello").U32(0xFFFFFFFF); break;                      // sign
                case 4: Tile(b, 2978, 1).U8(24).U32(242).I32(-5); break;                                  // vending
                case 5: Tile(b, 2, 2).U16(1); break;                                                      // locked tile
                case 6: Tile(b, 15546, 0).U32(4).Bytes(new byte[] { 0xA0, 1, 2, 3 }); break;              // auction (cbor)
                case 7: Tile(b, 8878, 1).U8(54).U16(26).Bytes(new byte[26]); break;                       // storage box
                case 8: Tile(b, 1420, 1).U8(48).Str16("Me").U32(0).U32(0).Bytes(new byte[5]).U8(0)        // portrait
                            .U16(0).U16(0).U16(12958).U16(0).U32(0).Str16("crown"); break;
                case 9: Tile(b, 15000, 1).U8(81).U32(1).U16(0).U16(2).U32(5).U32(6); break;               // friends entrance
                case 11: Tile(b, 5814, 1).U8(3).U8(0).U32(1).U32(0).U32(0).U32(0)                         // guild lock
                            .U32(9).U16(1).U16(2).U32(3).U32(4); break;
                case 12: Tile(b, 3394, 1).U8(33).Str16("fi"); break;                                      // country flag
                case 13: Tile(b, 7000, 1).U8(55).U32(2).U32(4).Bytes(new byte[16]).Bytes(new byte[12]); break; // oven
                default:
                    if (i >= 20 && i < 20 + bigSigns) Tile(b, 20, 1).U8(2).Str16(new string('x', 2000)).U32(0);
                    else Tile(b, 2, 0);
                    break;
            }
        }

        b.Bytes(new byte[12]);
        if (withObjects)
        {
            b.U32(3).U32(5);
            b.U16(112).F32(64).F32(96).U8(5).U8(0).U32(3);
            b.U16(242).F32(128).F32(96).U8(2).U8(0).U32(4);
            b.U16(2).F32(160).F32(96).U8(200).U8(0).U32(5);
        }
        else
        {
            b.U32(0).U32(0);
        }
        b.Bytes(new byte[20]);   // trailing world data (weather etc.)
        return b.ToArray();
    }

    private static Bin Tile(Bin b, int fg, int flags) => b.U16(fg).U16(14).U16(0).U16(flags);
}

internal sealed record Ev(int Type, IntPtr Peer, byte Channel, byte[] Data);

// A raw ENet endpoint configured exactly like the real game or the real server.
internal sealed class Node : IDisposable
{
    public IntPtr Host;
    public readonly List<Ev> Events = new();
    private readonly string _name;

    private Node(IntPtr host, string name)
    {
        if (host == IntPtr.Zero) throw new InvalidOperationException($"could not create {name}");
        Host  = host;
        _name = name;
    }

    // Real Growtopia server: expects the new header from clients, answers with the classic one.
    public static Node Server(string ip, ushort port) => new(AuraNative.aura_host_create(0, ip, port, 8), $"server {ip}:{port}");

    // Real Growtopia game: sends the new header.
    public static Node Game() => new(AuraNative.aura_host_create(1, null, 0, 2), "game");

    public void Service()
    {
        while (AuraNative.aura_host_service(Host, 0, out var e) > 0)
        {
            var data = e.Type == AuraNative.EventReceive ? AuraNative.CopyPacket(e) : Array.Empty<byte>();
            if (e.Type == AuraNative.EventReceive) AuraNative.aura_packet_destroy(e.Packet);
            Events.Add(new Ev(e.Type, e.Peer, e.Channel, data));
        }
    }

    public void Send(IntPtr peer, byte[] data, byte channel = 0) =>
        AuraNative.aura_peer_send(peer, channel, data, (uint)data.Length, AuraNative.PacketReliable);

    public void Dispose()
    {
        if (Host != IntPtr.Zero) AuraNative.aura_host_destroy(Host);
        Host = IntPtr.Zero;
    }

    public override string ToString() => _name;
}

internal static class Net
{
    public static Ev? Expect(Node target, int type, Node[] all, int timeoutMs = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            foreach (var n in all) n.Service();
            int i = target.Events.FindIndex(e => e.Type == type);
            if (i >= 0)
            {
                var ev = target.Events[i];
                target.Events.RemoveAt(i);
                return ev;
            }
            Thread.Sleep(1);
        }
        return null;
    }

    public static bool WaitUntil(Func<bool> condition, Node[] all, int timeoutMs = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            foreach (var n in all) n.Service();
            if (condition()) return true;
            Thread.Sleep(2);
        }
        return condition();
    }
}

// A fake "real" Growtopia login server (HTTPS, self-signed) whose answers are scripted per connection.
internal sealed class FakeLoginServer : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _cert;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<int, (int Status, string[] Headers, byte[] Body, bool Chunked)> _respond;
    public readonly List<(string Head, string Body)> Requests = new();
    public int Port { get; }

    public FakeLoginServer(int port, Func<int, (int, string[], byte[], bool)> respond)
    {
        _cert     = LoginRelay.CreateCertificate();
        _respond  = respond;
        Port      = port;
        _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        _listener.Start();
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        int index = 0;
        while (!_cts.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = HandleAsync(c, index++);
        }
    }

    private async Task HandleAsync(System.Net.Sockets.TcpClient c, int index)
    {
        using var _ = c;
        try
        {
            using var ssl = new System.Net.Security.SslStream(c.GetStream());
            await ssl.AuthenticateAsServerAsync(_cert);
            var req = await HttpWire.ReadRequestAsync(ssl, _cts.Token);
            if (req == null) return;
            lock (Requests) Requests.Add((Encoding.Latin1.GetString(req.RawHead), Encoding.Latin1.GetString(req.Body)));

            var (status, headers, body, chunked) = _respond(index);
            var head = new StringBuilder($"HTTP/1.1 {status} Scripted\r\n");
            foreach (var h in headers) head.Append(h).Append("\r\n");
            head.Append(chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {body.Length}\r\n").Append("\r\n");
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
            if (chunked)
            {
                int half = body.Length / 2;
                foreach (var part in new[] { body[..half], body[half..] })
                {
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes($"{part.Length:x}\r\n"));
                    await ssl.WriteAsync(part);
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                }
                await ssl.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
            }
            else
            {
                await ssl.WriteAsync(body);
            }
            await ssl.FlushAsync();
        }
        catch (Exception)
        {
            // The strict-certificate test makes the relay abort the handshake.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cert.Dispose();
    }
}

internal static class GameHttp
{
    public const string Body = "version=5.39&platform=0&protocol=225";

    // The request shape the Growtopia client sends (header order kept on purpose).
    public static string Head(string host = "www.growtopia1.com") =>
        "POST /growtopia/server_data.php HTTP/1.1\r\n" +
        $"Host: {host}\r\n" +
        "User-Agent: UbiServices_SDK_2022.Release.9_PC64_ansi_static\r\n" +
        "Accept: */*\r\n" +
        "Content-Type: application/x-www-form-urlencoded\r\n" +
        $"Content-Length: {Body.Length}\r\n\r\n";

    public static async Task<(int Status, string Body)> SendAsync(int relayPort)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, relayPort);
        using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("www.growtopia1.com");
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(Head() + Body));
        var resp = await HttpWire.ReadResponseAsync(ssl, "POST", CancellationToken.None);
        return (resp.Status, Encoding.Latin1.GetString(HttpWire.DecodeContent(resp)));
    }

    public static byte[] Gzip(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
            gz.Write(Encoding.ASCII.GetBytes(text));
        return ms.ToArray();
    }
}
