using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Text.Json;

namespace GrowTool77.Aura;

// Resolves host names through DNS-over-HTTPS (Cloudflare, Google as a
// fallback), exactly like the working GTProxy. This is needed because some
// internet providers answer Growtopia's domains with a wrong local address
// (the "Check your internet / VPN" symptom): the game — and a hosts-based
// proxy — then reach that wrong server instead of Growtopia's.
//
// The DoH servers are contacted at their fixed IP addresses so the machine's
// own (possibly wrong) resolver is never used, not even for the DoH host.
public sealed class DohResolver
{
    private sealed record Provider(string Host, string Path, IPAddress[] Ips);

    private static readonly Provider Cloudflare = new(
        "cloudflare-dns.com", "/dns-query",
        new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1") });

    private static readonly Provider Google = new(
        "dns.google", "/resolve",
        new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4") });

    private readonly AuraLog _log;

    public DohResolver(AuraLog log) => _log = log;

    // Returns the IPv4 address for host, or null if every provider failed.
    public async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;

        foreach (var provider in new[] { Cloudflare, Google })
        {
            try
            {
                var ip = await QueryAsync(provider, host, ct);
                if (ip != null) return ip;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException
                                          or AuthenticationException or JsonException
                                          or OperationCanceledException && !ct.IsCancellationRequested)
            {
                _log.Warn($"DoH via {provider.Host} failed for {host}: {ex.Message}");
            }
        }
        return null;
    }

    private async Task<IPAddress?> QueryAsync(Provider provider, string host, CancellationToken ct)
    {
        // Pin the connection to the provider's fixed IP; verify its real
        // certificate against its real host name so the DoH server is genuine.
        using var handler = new SocketsHttpHandler
        {
            UseProxy        = false,
            UseCookies      = false,
            ConnectTimeout  = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, token) =>
            {
                Exception? last = null;
                foreach (var ip in provider.Ips)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, 443), token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        last = ex;
                    }
                }
                throw last ?? new SocketException((int)SocketError.HostUnreachable);
            },
        };

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{provider.Host}{provider.Path}?name={Uri.EscapeDataString(host)}&type=A");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
        request.Headers.Host = provider.Host;

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            return null;

        // Prefer the last A record, as GTProxy does.
        IPAddress? result = null;
        foreach (var answer in answers.EnumerateArray())
        {
            if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 1 /* A */
                && answer.TryGetProperty("data", out var data)
                && IPAddress.TryParse(data.GetString(), out var ip)
                && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                result = ip;
            }
        }
        return result;
    }
}
