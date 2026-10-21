using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GrowTool77.Aura;

public sealed record LoginCheck(bool Ok, string Message);

// Local HTTPS endpoint for www.growtopia1.com / www.growtopia2.com while the
// hosts file points those names at 127.0.0.1.
//
// The game's request is forwarded byte-for-byte (same header order and
// casing) to the real login server, at the address that was looked up before
// the hosts change. Only the reply of server_data.php is changed: its
// server/port lines point at [AURA]. Request bodies are never logged.
public sealed class LoginRelay : IDisposable
{
    public const string SelfTestPath = "/aura-selftest";
    private const string ServerDataPath = "/growtopia/server_data.php";

    private readonly AuraLog _log;
    private readonly IReadOnlyDictionary<string, IPAddress> _upstream;
    private readonly Func<ushort> _auraPort;
    private readonly Action<string, ushort> _onServerData;
    private readonly int _upstreamPort;
    private readonly bool _strictUpstreamCertificate;
    private readonly X509Certificate2 _certificate;
    private readonly SslStreamCertificateContext _certificateContext;
    private readonly List<TcpListener> _listeners = new();
    private readonly CancellationTokenSource _cts = new();

    private volatile bool _allowTls13;          // starts with TLS 1.2 only, flips after a failed handshake
    private int _certificateWarned;
    private byte[]? _cachedReply;               // server_data reply fetched by PrefetchAsync
    private DateTime _cachedAt;

    public int HttpsPort { get; private set; }

    // Short status line for the UI ("Login step 1 failed: …").
    public event Action<string>? StepChanged;

    public LoginRelay(
        AuraLog log,
        IReadOnlyDictionary<string, IPAddress> upstream,
        Func<ushort> auraPort,
        Action<string, ushort> onServerData,
        int upstreamPort = 443,
        bool strictUpstreamCertificate = false)
    {
        _log                       = log;
        _upstream                  = upstream;
        _auraPort                  = auraPort;
        _onServerData              = onServerData;
        _upstreamPort              = upstreamPort;
        _strictUpstreamCertificate = strictUpstreamCertificate;
        _certificate               = CreateCertificate();
        _certificateContext        = SslStreamCertificateContext.Create(_certificate, additionalCertificates: null, offline: true);
    }

    public void Start(int httpsPort = 443, int httpPort = 80)
    {
        StartListener(httpsPort, tls: true, required: true);
        HttpsPort = httpsPort;
        if (httpPort > 0) StartListener(httpPort, tls: false, required: false);
    }

    private void StartListener(int port, bool tls, bool required)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            if (required) throw new InvalidOperationException($"[AURA] port {port} is already in use on this PC ({ex.SocketErrorCode}). Another program (for example another proxy, Skype or a local web server) is using it.");
            _log.Warn($"Port {port} is busy; continuing without it.");
            return;
        }
        _listeners.Add(listener);
        _log.Info($"Login endpoint listening on 127.0.0.1:{port} ({(tls ? "HTTPS" : "HTTP")}).");
        _ = AcceptLoopAsync(listener, tls);
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var l in _listeners) l.Stop();
        _listeners.Clear();
        _certificate.Dispose();
    }

    private void Step(string text, bool error = false)
    {
        if (error) _log.Error(text); else _log.Info(text);
        StepChanged?.Invoke(text);
    }

    // ── Checks run by AuraService before the hosts file is changed ─────────

    // Asks the real login server for server_data.php the way the game does.
    // A good reply is kept as a fallback for the game's own request.
    public async Task<LoginCheck> PrefetchAsync(string version, string protocol)
    {
        var host = _upstream.Keys.OrderBy(k => k.Contains("growtopia1") ? 0 : 1).FirstOrDefault();
        if (host == null) return new LoginCheck(false, "no login server address");

        var body = Encoding.ASCII.GetBytes($"version={version}&platform=0&protocol={protocol}");
        var head = Encoding.ASCII.GetBytes(
            $"POST {ServerDataPath} HTTP/1.1\r\n" +
            $"Host: {host}\r\n" +
            "User-Agent: UbiServices_SDK_2022.Release.9_PC64_ansi_static\r\n" +
            "Accept: */*\r\n" +
            "Content-Type: application/x-www-form-urlencoded\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await using var upstream = await OpenUpstreamAsync(host, timeout.Token);
            await upstream.WriteAsync(head, timeout.Token);
            await upstream.WriteAsync(body, timeout.Token);
            await upstream.FlushAsync(timeout.Token);
            var resp = await HttpWire.ReadResponseAsync(upstream, "POST", timeout.Token);
            var text = Encoding.Latin1.GetString(HttpWire.DecodeContent(resp));

            if (resp.Status != 200)
                return new LoginCheck(false, $"the real login server answered HTTP {resp.Status} {resp.Reason}" +
                    (resp.Status == 403 ? " (Growtopia refuses this internet connection right now)" : ""));
            if (!text.Contains("server|", StringComparison.Ordinal))
                return new LoginCheck(false, "the real login server answered without a server address (maintenance?)");

            _cachedReply = Encoding.Latin1.GetBytes(text);
            _cachedAt    = DateTime.UtcNow;
            var server = TextMessage.Value(text.Replace("\r", ""), "server") ?? "?";
            var port   = TextMessage.Value(text.Replace("\r", ""), "port") ?? "?";
            return new LoginCheck(true, $"real login server answered (game server {server}:{port})");
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or FormatException)
        {
            return new LoginCheck(false, $"the real login server could not be reached: {Describe(ex)}");
        }
    }

    // Connects to our own HTTPS endpoint to prove TLS works on this PC.
    public async Task<LoginCheck> SelfTestAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, HttpsPort, timeout.Token);
            await using var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "www.growtopia1.com",
                RemoteCertificateValidationCallback = (_, _, _, _) => true,   // it's our own certificate
            }, timeout.Token);
            var req = Encoding.ASCII.GetBytes($"GET {SelfTestPath} HTTP/1.1\r\nHost: www.growtopia1.com\r\nConnection: close\r\n\r\n");
            await ssl.WriteAsync(req, timeout.Token);
            var resp = await HttpWire.ReadResponseAsync(ssl, "GET", timeout.Token);
            return resp.Status == 200
                ? new LoginCheck(true, $"local secure endpoint works ({ssl.SslProtocol})")
                : new LoginCheck(false, $"local secure endpoint answered HTTP {resp.Status}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or FormatException)
        {
            return new LoginCheck(false, $"local secure endpoint failed: {Describe(ex)}");
        }
    }

    // ── Serving the game ───────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, bool tls)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = HandleAsync(client, tls);
        }
    }

    private async Task HandleAsync(TcpClient client, bool tls)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            Stream stream = client.GetStream();
            if (tls)
            {
                var ssl  = new SslStream(stream, leaveInnerStreamOpen: false);
                bool tls13 = _allowTls13;
                try
                {
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext  = _certificateContext,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols       = tls13 ? SslProtocols.Tls12 | SslProtocols.Tls13 : SslProtocols.Tls12,
                        ApplicationProtocols      = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                    }, timeout.Token);
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
                {
                    _allowTls13 = !tls13;
                    Step($"Login step 1 failed: secure connection with the game ({(tls13 ? "TLS 1.2/1.3" : "TLS 1.2")}) — {Describe(ex)}. " +
                         $"Next try: {(!tls13 ? "TLS 1.2/1.3" : "TLS 1.2")}.", error: true);
                    return;
                }
                _log.Info($"Login step 1 ok: secure connection ({ssl.SslProtocol}).");
                stream = ssl;
            }
            else
            {
                _log.Info("Login step 1 ok: plain HTTP connection.");
            }

            var request = await HttpWire.ReadRequestAsync(stream, timeout.Token);
            if (request == null)
            {
                _log.Warn("The game closed the login connection without asking anything.");
                return;
            }

            if (request.Path == SelfTestPath)
            {
                await HttpWire.WriteResponseAsync(stream, 200, "OK", "text/plain", Encoding.ASCII.GetBytes("aura-ok"), timeout.Token);
                return;
            }

            string path = request.Path.Split('?')[0];
            string host = (request.Header("Host") ?? "").Split(':')[0].Trim().ToLowerInvariant();
            _log.Info($"Login step 2 ok: the game asks {request.Method} {path} from {host}.");

            var reply = await AnswerAsync(request, host, path, timeout.Token);
            await HttpWire.WriteResponseAsync(stream, reply.Status, reply.Reason, reply.ContentType, reply.Body, timeout.Token, reply.ContentEncoding);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or AuthenticationException or SocketException or FormatException)
        {
            _log.Warn($"Login connection ended early: {Describe(ex)}.");
        }
    }

    private sealed record Reply(int Status, string Reason, string ContentType, byte[] Body, string? ContentEncoding = null);

    private async Task<Reply> AnswerAsync(HttpWire.Request req, string host, string path, CancellationToken ct)
    {
        bool isServerData = path.Equals(ServerDataPath, StringComparison.OrdinalIgnoreCase);

        HttpWire.Response? resp = null;
        string? failure = null;
        if (!_upstream.ContainsKey(host))
        {
            failure = $"no address known for '{host}'";
        }
        else
        {
            using var upTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            upTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await using var upstream = await OpenUpstreamAsync(host, upTimeout.Token);
                await upstream.WriteAsync(req.HeadForForwarding(), upTimeout.Token);
                if (req.Body.Length > 0) await upstream.WriteAsync(req.Body, upTimeout.Token);
                await upstream.FlushAsync(upTimeout.Token);
                resp = await HttpWire.ReadResponseAsync(upstream, req.Method, upTimeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or FormatException
                                          || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                failure = ex is OperationCanceledException
                    ? "the real login server did not answer in 15 seconds"
                    : $"the real login server could not be reached: {Describe(ex)}";
            }
        }

        if (!isServerData)
        {
            if (resp == null)
            {
                Step($"Login step 3 failed for {path}: {failure}.", error: true);
                return new Reply(502, "Bad Gateway", "text/plain", Encoding.ASCII.GetBytes("[AURA] could not reach the server"));
            }
            _log.Info($"Login step 3: {path} → HTTP {resp.Status}.");
            return new Reply(resp.Status, resp.Reason, resp.Header("Content-Type") ?? "text/html", resp.Body, resp.Header("Content-Encoding"));
        }

        // server_data.php: rewrite a good reply, or fall back to the one fetched at start.
        byte[]? text = null;
        if (resp != null && resp.Status == 200)
        {
            var decoded = HttpWire.DecodeContent(resp);
            if (Encoding.Latin1.GetString(decoded).Contains("server|", StringComparison.Ordinal)) text = decoded;
            else failure = "the real login server answered without a server address";
        }
        else if (resp != null)
        {
            failure = $"the real login server answered HTTP {resp.Status} {resp.Reason}";
        }

        if (text == null && _cachedReply != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(15))
        {
            _log.Warn($"Login step 3: {failure}. Using the reply [AURA] fetched when it started.");
            text = _cachedReply;
        }

        if (text == null)
        {
            Step($"Login step 3 failed: {failure}.", error: true);
            return resp != null
                ? new Reply(resp.Status, resp.Reason, resp.Header("Content-Type") ?? "text/html", resp.Body, resp.Header("Content-Encoding"))
                : new Reply(502, "Bad Gateway", "text/plain", Encoding.ASCII.GetBytes("[AURA] could not reach the server"));
        }

        var rewritten = RewriteServerData(text);
        return new Reply(200, "OK", resp?.Header("Content-Type") ?? "text/html", rewritten);
    }

    private byte[] RewriteServerData(byte[] body)
    {
        var text  = Encoding.Latin1.GetString(body);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        string? server = null;
        ushort  port   = 0;
        bool    type2  = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("server|", StringComparison.Ordinal))
            {
                server   = line[7..].Trim();
                lines[i] = "server|" + AuraProxy.Loopback;
            }
            else if (line.StartsWith("port|", StringComparison.Ordinal))
            {
                ushort.TryParse(line[5..].Trim(), out port);
                lines[i] = "port|" + _auraPort();
            }
            else if (line.StartsWith("type2|", StringComparison.Ordinal))
            {
                lines[i] = "type2|1";
                type2 = true;
            }
        }

        if (server == null || port == 0)
        {
            _log.Warn("The login server's reply had no usable server address. Passed through unchanged.");
            return body;
        }

        if (!type2)
        {
            int end = lines.Count;
            while (end > 0 && lines[end - 1].Length == 0) end--;
            lines.Insert(end, "type2|1");
        }

        _onServerData(server, port);
        Step($"Login step 3 ok: game server is {server}:{port}; the game is sent to [AURA] instead.");
        return Encoding.Latin1.GetBytes(string.Join("\n", lines));
    }

    // TLS connection to the real login server at the address resolved before
    // the hosts change. Windows checks the certificate; if Windows doesn't
    // trust it, [AURA] continues like Growtopia itself does (the game accepts
    // any certificate) unless strict mode is on, and says so in the log.
    private async Task<Stream> OpenUpstreamAsync(string host, CancellationToken ct)
    {
        if (!_upstream.TryGetValue(host, out var ip)) throw new IOException($"no address known for {host}");

        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new IPEndPoint(ip, _upstreamPort), connect.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
        var seen = SslPolicyErrors.None;
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost                     = host,
                ApplicationProtocols           = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                {
                    seen = errors;
                    return errors == SslPolicyErrors.None || !_strictUpstreamCertificate;
                },
            }, ct);
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }

        if (seen != SslPolicyErrors.None && Interlocked.Exchange(ref _certificateWarned, 1) == 0)
            _log.Warn($"Windows does not trust the login server's certificate ({seen}). Continuing anyway, like Growtopia itself does.");
        return ssl;
    }

    // "Outer message → inner message → …" without stack traces.
    internal static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
        {
            var msg = e.Message.Trim().TrimEnd('.');
            if (msg.Length > 0 && !parts.Contains(msg)) parts.Add(msg);
        }
        return string.Join(" → ", parts);
    }

    // Self-signed certificate created in memory for this session only.
    internal static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=www.growtopia1.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("www.growtopia1.com");
        san.AddDnsName("www.growtopia2.com");
        san.AddDnsName("growtopia1.com");
        san.AddDnsName("growtopia2.com");
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        using var temp = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Re-import so Windows' TLS stack gets a usable private key.
#pragma warning disable SYSLIB0057
        return new X509Certificate2(temp.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
    }
}

// Minimal HTTP/1.1 reader/writer.
internal static class HttpWire
{
    public sealed class Request
    {
        public string Method = "";
        public string Path   = "/";
        public List<(string Name, string Value)> Headers = new();
        public byte[] RawHead = Array.Empty<byte>();
        public byte[] Body    = Array.Empty<byte>();
        public bool   WasChunked;

        public string? Header(string name) =>
            Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

        // The game's own header bytes. Only a chunked body is re-framed with a length.
        public byte[] HeadForForwarding()
        {
            if (!WasChunked) return RawHead;
            var lines = Encoding.Latin1.GetString(RawHead).Split("\r\n").ToList();
            lines.RemoveAll(l => l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase));
            int blank = lines.FindIndex(1, l => l.Length == 0);
            lines.Insert(blank < 0 ? lines.Count : blank, $"Content-Length: {Body.Length}");
            return Encoding.Latin1.GetBytes(string.Join("\r\n", lines));
        }
    }

    public sealed class Response
    {
        public int    Status;
        public string Reason = "";
        public List<(string Name, string Value)> Headers = new();
        public byte[] Body = Array.Empty<byte>();

        public string? Header(string name) =>
            Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private const int MaxBody = 4 * 1024 * 1024;

    private static async Task<byte[]?> ReadHeadAsync(Stream s, CancellationToken ct)
    {
        var head = new MemoryStream();
        var one  = new byte[1];
        while (true)
        {
            int n = await s.ReadAsync(one, ct);
            if (n == 0) return head.Length == 0 ? null : throw new IOException("Connection closed in the middle of a header.");
            head.WriteByte(one[0]);
            if (head.Length > 65536) throw new FormatException("Header too large.");
            if (head.Length >= 4)
            {
                var b = head.GetBuffer();
                long l = head.Length;
                if (b[l - 4] == '\r' && b[l - 3] == '\n' && b[l - 2] == '\r' && b[l - 1] == '\n') return head.ToArray();
            }
        }
    }

    private static List<(string, string)> ParseHeaders(string[] lines)
    {
        var list = new List<(string, string)>();
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) list.Add((line[..c].Trim(), line[(c + 1)..].Trim()));
        }
        return list;
    }

    public static async Task<Request?> ReadRequestAsync(Stream s, CancellationToken ct)
    {
        var raw = await ReadHeadAsync(s, ct);
        if (raw == null) return null;

        var lines = Encoding.Latin1.GetString(raw).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) throw new FormatException("Bad request line.");
        var req = new Request { Method = first[0].ToUpperInvariant(), Path = first[1], RawHead = raw, Headers = ParseHeaders(lines) };

        if ((req.Header("Transfer-Encoding") ?? "").Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            req.Body = await ReadChunkedAsync(s, ct);
            req.WasChunked = true;
        }
        else if (int.TryParse(req.Header("Content-Length"), out int len) && len > 0)
        {
            if (len > MaxBody) throw new FormatException("Body too large.");
            req.Body = new byte[len];
            await s.ReadExactlyAsync(req.Body, ct);
        }
        return req;
    }

    public static async Task<Response> ReadResponseAsync(Stream s, string method, CancellationToken ct)
    {
        while (true)
        {
            var raw = await ReadHeadAsync(s, ct) ?? throw new IOException("The server closed the connection without answering.");
            var lines = Encoding.Latin1.GetString(raw).Split("\r\n");
            var first = lines[0].Split(' ', 3);
            if (first.Length < 2 || !first[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(first[1], out int status))
                throw new FormatException("Bad status line.");

            var resp = new Response { Status = status, Reason = first.Length > 2 ? first[2] : "", Headers = ParseHeaders(lines) };
            if (status is >= 100 and < 200) continue;            // e.g. 100 Continue
            if (method == "HEAD" || status is 204 or 304) return resp;

            if ((resp.Header("Transfer-Encoding") ?? "").Contains("chunked", StringComparison.OrdinalIgnoreCase))
                resp.Body = await ReadChunkedAsync(s, ct);
            else if (long.TryParse(resp.Header("Content-Length"), out long len))
            {
                if (len < 0 || len > MaxBody) throw new FormatException("Body too large.");
                resp.Body = new byte[len];
                await s.ReadExactlyAsync(resp.Body, ct);
            }
            else
            {
                var ms  = new MemoryStream();
                var buf = new byte[8192];
                int n;
                while ((n = await s.ReadAsync(buf, ct)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxBody) throw new FormatException("Body too large.");
                }
                resp.Body = ms.ToArray();
            }
            return resp;
        }
    }

    // Undoes gzip / deflate / br content encoding.
    public static byte[] DecodeContent(Response resp)
    {
        var enc = (resp.Header("Content-Encoding") ?? "").Trim().ToLowerInvariant();
        if (enc is "" or "identity") return resp.Body;
        using var input = new MemoryStream(resp.Body);
        using Stream decoder = enc switch
        {
            "gzip"    => new GZipStream(input, CompressionMode.Decompress),
            "deflate" => new ZLibStream(input, CompressionMode.Decompress),
            "br"      => new BrotliStream(input, CompressionMode.Decompress),
            _         => throw new FormatException($"Unknown content encoding '{enc}'."),
        };
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static async Task<byte[]> ReadChunkedAsync(Stream s, CancellationToken ct)
    {
        var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(s, ct);
            int size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
            if (size == 0)
            {
                while ((await ReadLineAsync(s, ct)).Length > 0) { }   // trailers
                break;
            }
            if (body.Length + size > MaxBody) throw new FormatException("Body too large.");
            var chunk = new byte[size];
            await s.ReadExactlyAsync(chunk, ct);
            body.Write(chunk);
            await ReadLineAsync(s, ct);
        }
        return body.ToArray();
    }

    private static async Task<string> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var sb  = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0) throw new IOException("Connection closed.");
            if (one[0] == '\n') break;
            if (one[0] != '\r') sb.Append((char)one[0]);
            if (sb.Length > 1024) throw new FormatException("Line too long.");
        }
        return sb.ToString();
    }

    public static async Task WriteResponseAsync(Stream s, int status, string reason, string contentType, byte[] body,
                                                CancellationToken ct, string? contentEncoding = null)
    {
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append($"Content-Type: {contentType}\r\n");
        if (!string.IsNullOrEmpty(contentEncoding)) head.Append($"Content-Encoding: {contentEncoding}\r\n");
        head.Append($"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }
}
