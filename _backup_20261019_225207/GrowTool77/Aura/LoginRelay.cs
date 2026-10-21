using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GrowTool77.Aura;

// Local HTTPS endpoint for www.growtopia1.com / www.growtopia2.com while the
// hosts file points those names at 127.0.0.1. Each request is forwarded to
// the real Growtopia server (address resolved before the hosts change) with
// normal certificate checks. Only server_data.php is changed: its
// server/port lines are pointed at [AURA]. Nothing is logged except the
// request path and the server address.
public sealed class LoginRelay : IDisposable
{
    private readonly AuraLog _log;
    private readonly IReadOnlyDictionary<string, IPAddress> _upstream;
    private readonly Func<ushort> _auraPort;
    private readonly Action<string, ushort> _onServerData;
    private readonly int _upstreamPort;
    private readonly HttpClient _http;
    private readonly X509Certificate2 _certificate;
    private readonly List<TcpListener> _listeners = new();
    private readonly CancellationTokenSource _cts = new();

    public LoginRelay(
        AuraLog log,
        IReadOnlyDictionary<string, IPAddress> upstream,
        Func<ushort> auraPort,
        Action<string, ushort> onServerData,
        int upstreamPort = 443,
        bool validateUpstreamCertificate = true)
    {
        _log          = log;
        _upstream     = upstream;
        _auraPort     = auraPort;
        _onServerData = onServerData;
        _upstreamPort = upstreamPort;
        _certificate  = CreateCertificate();

        var handler = new SocketsHttpHandler
        {
            UseProxy               = false,
            UseCookies             = false,
            AllowAutoRedirect      = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout         = TimeSpan.FromSeconds(10),
            ConnectCallback        = ConnectUpstreamAsync,
        };
        if (!validateUpstreamCertificate)   // test suite only
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public void Start(int httpsPort = 443, int httpPort = 80)
    {
        StartListener(httpsPort, tls: true, required: true);
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
            if (required) throw new InvalidOperationException($"[AURA] port {port} is already in use on this PC ({ex.SocketErrorCode}).");
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
        _http.Dispose();
        _certificate.Dispose();
    }

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
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                try
                {
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate         = _certificate,
                        ClientCertificateRequired = false,
                    }, timeout.Token);
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
                {
                    _log.Error($"Login step 1 failed: secure connection with Growtopia could not be set up ({Describe(ex)}).");
                    return;
                }
                _log.Info($"Login step 1 ok: Growtopia connected securely ({ssl.SslProtocol}).");
                stream = ssl;
            }
            else
            {
                _log.Info("Login step 1 ok: Growtopia connected (plain HTTP).");
            }

            var request = await HttpWire.ReadRequestAsync(stream, timeout.Token);
            if (request == null)
            {
                _log.Warn("Growtopia closed the login connection without asking anything.");
                return;
            }
            _log.Info($"Login step 2: Growtopia asks {request.Method} {request.Path.Split('?')[0]} from {request.Header("Host") ?? "?"}.");

            var (status, reason, contentType, body) = await ForwardAsync(request, tls, timeout.Token);
            await HttpWire.WriteResponseAsync(stream, status, reason, contentType, body, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or AuthenticationException or SocketException or FormatException)
        {
            _log.Warn($"Login connection ended early: {Describe(ex)}.");
        }
    }

    private async Task<(int, string, string, byte[])> ForwardAsync(HttpWire.Request req, bool tls, CancellationToken ct)
    {
        string host = (req.Header("Host") ?? "").Split(':')[0].Trim().ToLowerInvariant();
        if (!_upstream.ContainsKey(host))
        {
            _log.Error($"Login step 3 failed: no address known for '{host}'.");
            return (404, "Not Found", "text/plain", Encoding.ASCII.GetBytes("Unknown host"));
        }

        var uri = new UriBuilder(tls ? "https" : "http", host, _upstreamPort, req.Path.Split('?')[0])
        {
            Query = req.Path.Contains('?') ? req.Path[(req.Path.IndexOf('?') + 1)..] : "",
        }.Uri;
        if (_upstreamPort == (tls ? 443 : 80)) uri = new UriBuilder(uri) { Port = -1 }.Uri;

        using var msg = new HttpRequestMessage(new HttpMethod(req.Method), uri);
        if (req.Body.Length > 0 || req.Method is "POST" or "PUT")
        {
            msg.Content = new ByteArrayContent(req.Body);
            var ctype = req.Header("Content-Type");
            if (ctype != null && MediaTypeHeaderValue.TryParse(ctype, out var parsed))
                msg.Content.Headers.ContentType = parsed;
        }
        foreach (var (name, value) in req.Headers)
        {
            if (HttpWire.IsHopByHop(name) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            msg.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(msg, ct);
        }
        catch (HttpRequestException ex)
        {
            _log.Error($"Login step 3 failed: the real Growtopia login server could not be reached ({Describe(ex)}).");
            return (502, "Bad Gateway", "text/plain", Encoding.ASCII.GetBytes("[AURA] could not reach the server"));
        }

        using (resp)
        {
            var body  = await resp.Content.ReadAsByteArrayAsync(ct);
            var ctype = resp.Content.Headers.ContentType?.ToString() ?? "text/html";

            if (req.Path.StartsWith("/growtopia/server_data.php", StringComparison.OrdinalIgnoreCase) && resp.IsSuccessStatusCode)
                body = RewriteServerData(body);
            else
                _log.Warn($"Login step 3: the real login server answered {(int)resp.StatusCode} {resp.ReasonPhrase} for {req.Path.Split('?')[0]}.");

            return ((int)resp.StatusCode, resp.ReasonPhrase ?? "OK", ctype, body);
        }
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
            _log.Warn("The login server's reply had no server address (maintenance?). Passed through unchanged.");
            return body;
        }

        if (!type2)
        {
            int end = lines.Count;
            while (end > 0 && lines[end - 1].Length == 0) end--;
            lines.Insert(end, "type2|1");
        }

        _onServerData(server, port);
        _log.Info($"Login step 3 ok: real game server is {server}:{port}; Growtopia is sent to [AURA] instead.");
        return Encoding.Latin1.GetBytes(string.Join("\n", lines));
    }

    private async ValueTask<Stream> ConnectUpstreamAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        if (!_upstream.TryGetValue(ctx.DnsEndPoint.Host.ToLowerInvariant(), out var ip))
            throw new HttpRequestException($"[AURA] refuses to connect to {ctx.DnsEndPoint.Host}.");

        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, _upstreamPort), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // "Outer message → inner message → …" without stack traces.
    private static string Describe(Exception ex)
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

// Minimal HTTP/1.1 reader/writer: one request per connection.
internal static class HttpWire
{
    public sealed class Request
    {
        public string Method = "";
        public string Path   = "/";
        public List<(string Name, string Value)> Headers = new();
        public byte[] Body = Array.Empty<byte>();

        public string? Header(string name) =>
            Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
    };

    public static bool IsHopByHop(string name) => HopByHop.Contains(name);

    public static async Task<Request?> ReadRequestAsync(Stream s, CancellationToken ct)
    {
        var head = new MemoryStream();
        var one  = new byte[1];
        while (true)
        {
            int n = await s.ReadAsync(one, ct);
            if (n == 0) return null;
            head.WriteByte(one[0]);
            if (head.Length > 65536) throw new FormatException("Header too large.");
            if (head.Length >= 4)
            {
                var b = head.GetBuffer();
                long l = head.Length;
                if (b[l - 4] == '\r' && b[l - 3] == '\n' && b[l - 2] == '\r' && b[l - 1] == '\n') break;
            }
        }

        var lines = Encoding.Latin1.GetString(head.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) throw new FormatException("Bad request line.");
        var req = new Request { Method = first[0].ToUpperInvariant(), Path = first[1] };
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) req.Headers.Add((line[..c].Trim(), line[(c + 1)..].Trim()));
        }

        if ((req.Header("Transfer-Encoding") ?? "").Contains("chunked", StringComparison.OrdinalIgnoreCase))
            req.Body = await ReadChunkedAsync(s, ct);
        else if (int.TryParse(req.Header("Content-Length"), out int len) && len > 0)
        {
            if (len > 1_048_576) throw new FormatException("Body too large.");
            req.Body = new byte[len];
            await s.ReadExactlyAsync(req.Body, ct);
        }
        return req;
    }

    private static async Task<byte[]> ReadChunkedAsync(Stream s, CancellationToken ct)
    {
        var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(s, ct);
            int size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
            if (size == 0) { await ReadLineAsync(s, ct); break; }
            if (body.Length + size > 1_048_576) throw new FormatException("Body too large.");
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

    public static async Task WriteResponseAsync(Stream s, int status, string reason, string contentType, byte[] body, CancellationToken ct)
    {
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }
}
