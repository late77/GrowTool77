using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AuraTests;
using GrowTool77.Aura;

int passed = 0, failed = 0;

void Check(bool ok, string what)
{
    if (ok) passed++; else failed++;
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
}

void Section(string name) => Console.WriteLine($"\n== {name}");

// ─────────────────────────────────────────────────────────────────────────
Section("items.dat parser (real files from three public repositories)");
string research = args.Length > 0 ? args[0] : "/home/claude/research";
foreach (var rel in new[] { "GTProxy/resources/items.dat", "grow-items/assets/items.dat", "Growtopia-Things/parsers/items.dat" })
{
    var path = Path.Combine(research, rel);
    if (!File.Exists(path)) { Console.WriteLine($"  skip {rel} (missing)"); continue; }
    var bytes = File.ReadAllBytes(path);
    try
    {
        var db = ItemDatabase.Parse(bytes);
        int declared = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(2));
        Check(db.ParsedLength == bytes.Length, $"{rel}: v{db.Version}, {db.Count} items, consumed {db.ParsedLength}/{bytes.Length} bytes");
        Check(db.Count == declared, $"{rel}: item count matches header ({declared})");
        Check(db.NameOf(2) == "Dirt" && db.NameOf(112) == "Gems" && db.NameOf(242) == "World Lock",
              $"{rel}: names 2/112/242 = {db.NameOf(2)} / {db.NameOf(112)} / {db.NameOf(242)}");
    }
    catch (Exception ex)
    {
        Check(false, $"{rel}: {ex.GetType().Name} {ex.Message}");
    }
}

// ─────────────────────────────────────────────────────────────────────────
Section("world parser");
{
    var world = Worlds.Build();
    var snap  = WorldParser.Parse(world, 0, world.Length, null);
    Check(snap.Name == "AURATEST", $"name = {snap.Name}");
    Check(snap.Width == 100 && snap.Height == 60, $"size = {snap.Width}x{snap.Height}");
    Check(snap.TilesParsed, $"all tiles parsed exactly (note: {snap.Note ?? "none"})");
    Check(snap.Objects.Count == 3 && snap.LastObjectUid == 5, $"objects = {snap.Objects.Count}, last uid = {snap.LastObjectUid}");
    Check(snap.Objects.Any(o => o.ItemId == 112 && o.Amount == 5), "5 gems found");

    var broken = Worlds.Build(unknownAt: 30);
    var snap2  = WorldParser.Parse(broken, 0, broken.Length, null);
    Check(!snap2.TilesParsed && snap2.Objects.Count == 3, $"unknown tile type → fallback still finds 3 objects ({snap2.Note})");

    var empty = Worlds.Build(unknownAt: 30, withObjects: false);
    var snap3 = WorldParser.Parse(empty, 0, empty.Length, null);
    Check(snap3.Objects.Count == 0 && snap3.Note != null, $"no objects + unknown tile → reported honestly ({snap3.Note})");

    var garbage = new byte[64];
    RandomNumberGenerator.Fill(garbage);
    bool threw = false;
    try { WorldParser.Parse(garbage, 0, garbage.Length, null); } catch (FormatException) { threw = true; }
    Check(threw, "random bytes are rejected with FormatException (no crash)");
}

// ─────────────────────────────────────────────────────────────────────────
Section("world tracker (live drop updates)");
{
    var world = Worlds.Build();
    var t = new WorldTracker();
    t.Enter(WorldParser.Parse(world, 0, world.Length, null));
    Check(t.Snapshot()!.Gems == 5, "start: 5 gems");

    GameUpdate.TryRead(Pkt.Game(14, netId: -1, floatVar: 10f, intData: 112, x: 200, y: 300), out var add);
    t.Apply(add);
    Check(t.Snapshot()!.Gems == 15, "new drop (float amount) → 15 gems");

    var intAmount = Pkt.Game(14, netId: -1, intData: 242, x: 10, y: 10);
    BinaryPrimitives.WriteInt32LittleEndian(intAmount.AsSpan(20), 3);
    GameUpdate.TryRead(intAmount, out var addInt);
    t.Apply(addInt);
    Check(t.Snapshot()!.Items.First(i => i.ItemId == 242).Amount == 5, "new drop (integer amount) → 5 World Locks");

    GameUpdate.TryRead(Pkt.Game(14, netId: -3, itemNetId: 3, floatVar: 50f, intData: 112), out var mod);
    t.Apply(mod);
    Check(t.Snapshot()!.Gems == 60, "changed drop → 60 gems");

    GameUpdate.TryRead(Pkt.Game(14, netId: 7, itemNetId: 6), out var pick);
    t.Apply(pick);
    Check(t.Snapshot()!.Gems == 50, "picked up → 50 gems");

    t.Leave();
    Check(t.Snapshot() == null, "leaving clears the world");
}

// ─────────────────────────────────────────────────────────────────────────
Section("variant rewrite is byte-exact");
{
    var original = Pkt.OnSendToServer(17198, "213.179.209.168|0|UUID");
    GameUpdate.TryRead(original, out var gu);
    VariantList.TryParse(original, GameUpdate.ExtraStart, gu.ExtraLength, out var vl);
    var same = VariantList.RebuildCallFunction(original, gu, vl.Serialize());
    Check(same.AsSpan().SequenceEqual(original), "parse + serialize reproduces the original packet");
}

// ─────────────────────────────────────────────────────────────────────────
Section("hosts file block");
{
    var original = "# Copyright (c) Microsoft\r\n127.0.0.1 localhost\r\n";
    var added    = HostsFile.AddBlock(original);
    Check(added.Contains("127.0.0.1 www.growtopia1.com") && added.StartsWith(original.TrimEnd()), "block added after existing lines");
    Check(HostsFile.AddBlock(added) == added, "adding twice doesn't duplicate");
    Check(HostsFile.RemoveBlock(added) == original, "removing restores the original exactly");
    Check(HostsFile.RemoveBlock(original) == original, "removing when absent changes nothing");
}

// ─────────────────────────────────────────────────────────────────────────
Section("[AURA] relay end-to-end over real UDP (game ⇄ [AURA] ⇄ two servers)");
{
    AuraNative.EnsureInitialized();
    var log   = new AuraLog();
    var proxy = new AuraProxy(log);
    WorldView? lastWorld = null;
    int worldEvents = 0;
    proxy.WorldChanged += w => { lastWorld = w; Interlocked.Increment(ref worldEvents); };
    proxy.Start(26999);
    ushort P = proxy.Port;

    using var serverA = Node.Server("127.0.0.1", 27101);
    using var serverB = Node.Server("127.0.0.2", 27102);
    using var game    = Node.Game();
    var all = new[] { serverA, serverB, game };

    proxy.SetServerTarget("127.0.0.1", 27101);
    var toProxy = AuraNative.aura_host_connect(game.Host, "127.0.0.1", P);
    var gConn   = Net.Expect(game, AuraNative.EventConnect, all);
    var aConn   = Net.Expect(serverA, AuraNative.EventConnect, all);
    Check(gConn != null && aConn != null, "game → [AURA] → server A connected (new integrity header accepted)");

    if (aConn != null)
    {
        serverA.Send(aConn.Peer, new byte[] { 1, 0, 0, 0 });
        var hello = Net.Expect(game, AuraNative.EventReceive, all);
        Check(hello != null && hello.Data.SequenceEqual(new byte[] { 1, 0, 0, 0 }), "server hello reaches the game");

        var login = Pkt.Text(2, "protocol|225\nltoken|SECRET-TOKEN-123\nrequestedName|tester\n");
        game.Send(toProxy, login);
        var got = Net.Expect(serverA, AuraNative.EventReceive, all);
        Check(got != null && got.Data.SequenceEqual(login), "login packet reaches the server unchanged");

        serverA.Send(aConn.Peer, Pkt.OnSendToServer(27102, "127.0.0.2|0|UUID-SECRET"));
        var sw = Net.Expect(game, AuraNative.EventReceive, all);
        bool rewritten = false;
        if (sw != null && GameUpdate.TryRead(sw.Data, out var gu) &&
            VariantList.TryParse(sw.Data, GameUpdate.ExtraStart, gu.ExtraLength, out var vl))
        {
            rewritten = vl.GetInt(1) == P && vl.Items[4].S == "127.0.0.1|0|UUID-SECRET"
                     && vl.GetInt(2) == 777 && vl.GetInt(3) == 4242 && vl.Items[6].S == "tester";
        }
        Check(rewritten, "OnSendToServer points the game back at [AURA], other fields untouched");

        AuraNative.aura_peer_disconnect(aConn.Peer, 0);
        AuraNative.aura_peer_disconnect(toProxy, 0);
        Net.Expect(game, AuraNative.EventDisconnect, all);

        var toProxy2 = AuraNative.aura_host_connect(game.Host, "127.0.0.1", P);
        var g2 = Net.Expect(game, AuraNative.EventConnect, all);
        var bConn = Net.Expect(serverB, AuraNative.EventConnect, all);
        Check(g2 != null && bConn != null, "game reconnects through [AURA] to sub-server B (127.0.0.2)");

        if (bConn != null)
        {
            var join = Pkt.Text(3, "action|join_request\nname|auratest\ninvitedWorld|0\n");
            game.Send(toProxy2, join);
            var j = Net.Expect(serverB, AuraNative.EventReceive, all);
            Check(j != null && j.Data.SequenceEqual(join), "join request reaches server B");

            var worldBytes = Worlds.Build();
            var map = Pkt.Game(4, extra: worldBytes);
            serverB.Send(bConn.Peer, map);
            var m = Net.Expect(game, AuraNative.EventReceive, all, 15000);
            Check(m != null && SHA256.HashData(m.Data).SequenceEqual(SHA256.HashData(map)),
                  $"{map.Length / 1024} KB world packet arrives intact (fragmented by ENet)");

            Check(Net.WaitUntil(() => lastWorld?.Name == "AURATEST", all), $"[AURA] sees world {lastWorld?.Name}");
            Check(lastWorld?.Gems == 5 && lastWorld.TotalItems == 202 && lastWorld.Exact,
                  $"scan: {lastWorld?.Gems} gems, {lastWorld?.TotalItems} items, exact={lastWorld?.Exact}");

            serverB.Send(bConn.Peer, Pkt.Game(14, netId: -1, floatVar: 10f, intData: 112, x: 200, y: 300));
            Check(Net.WaitUntil(() => lastWorld?.Gems == 15, all), "live drop: gems 5 → 15");

            serverB.Send(bConn.Peer, Pkt.Game(14, netId: 7, itemNetId: 3));
            Check(Net.WaitUntil(() => lastWorld?.Gems == 10, all), "live pickup: gems 15 → 10");

            var bigUp = new byte[40_000];
            new Random(7).NextBytes(bigUp);
            BinaryPrimitives.WriteInt32LittleEndian(bigUp, 2);
            game.Send(toProxy2, bigUp);
            var up = Net.Expect(serverB, AuraNative.EventReceive, all, 15000);
            Check(up != null && up.Data.AsSpan().SequenceEqual(bigUp), "40 KB packet game → [AURA] → server arrives intact (integrity header per fragment)");

            game.Send(toProxy2, Pkt.Text(3, "action|quit_to_exit\n"));
            Check(Net.WaitUntil(() => lastWorld == null, all), "leaving the world clears the scan");
        }
    }

    var lines = string.Join("\n", log.Lines());
    Check(!lines.Contains("SECRET"), "log never contains the login token or uuid token");
    Check(!lines.Contains("tester"), "log never contains the account name");

    proxy.Stop();
    Check(!proxy.IsRunning, "[AURA] stops cleanly");
    Console.WriteLine("  --- [AURA] log ---");
    foreach (var l in log.Lines()) Console.WriteLine("  | " + l);
}

// ─────────────────────────────────────────────────────────────────────────
Section("login relay (HTTPS server_data.php)");
{
    const string reply = "server|213.179.209.168\nport|17198\ntype|1\n#maint|Maintenance\nmeta|abc123\nRTENDMARKERBS1001\n";

    async Task<(string? Body, (string, ushort)? Target, string? SeenHost, string? SeenBody)> RunAsync(bool validate, int relayPort, int upPort)
    {
        using var cert     = LoginRelay.CreateCertificate();
        var upstream       = new TcpListener(IPAddress.Loopback, upPort);
        upstream.Start();
        string? seenHost = null, seenBody = null;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var upTask = Task.Run(async () =>
        {
            try
            {
                using var c   = await upstream.AcceptTcpClientAsync(cts.Token);
                using var ssl = new SslStream(c.GetStream());
                await ssl.AuthenticateAsServerAsync(cert);
                var req = await HttpWire.ReadRequestAsync(ssl, cts.Token);
                seenHost = req?.Header("Host");
                seenBody = req == null ? null : Encoding.ASCII.GetString(req.Body);
                await HttpWire.WriteResponseAsync(ssl, 200, "OK", "text/html", Encoding.ASCII.GetBytes(reply), cts.Token);
            }
            catch (Exception) { /* validation test: client aborts the handshake */ }
        });

        (string, ushort)? target = null;
        var log = new AuraLog();
        using var relay = new LoginRelay(log,
            new Dictionary<string, IPAddress> { ["www.growtopia1.com"] = IPAddress.Loopback },
            () => 16999, (ip, port) => target = (ip, port), upstreamPort: upPort, validateUpstreamCertificate: validate);
        relay.Start(httpsPort: relayPort, httpPort: 0);

        using var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });
        var msg = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{relayPort}/growtopia/server_data.php")
        {
            Content = new StringContent("version=5.39&platform=0&protocol=225", Encoding.ASCII, "application/x-www-form-urlencoded"),
        };
        msg.Headers.Host = "www.growtopia1.com";
        var resp = await client.SendAsync(msg);
        var body = await resp.Content.ReadAsStringAsync();
        await Task.WhenAny(upTask, Task.Delay(3000));
        upstream.Stop();
        return (resp.IsSuccessStatusCode ? body : null, target, seenHost, seenBody);
    }

    var (body, target, seenHost, seenBody) = await RunAsync(validate: false, 18443, 18444);
    Check(seenHost?.Split(':')[0] == "www.growtopia1.com" && seenBody == "version=5.39&platform=0&protocol=225",
          $"request reaches the real server with the right Host and body (host='{seenHost}', body='{seenBody}')");
    Check(body != null && body.Contains("server|127.0.0.1\n") && body.Contains("port|16999\n") && body.Contains("type2|1"),
          "reply tells the game to connect to [AURA]");
    Check(body != null && body.Contains("meta|abc123") && body.Contains("#maint|Maintenance") && body.Contains("RTENDMARKERBS1001"),
          "all other lines are passed through unchanged");
    Check(target == ("213.179.209.168", (ushort)17198), $"[AURA] learns the real server {target}");

    var (body2, target2, _, _) = await RunAsync(validate: true, 18445, 18446);
    Check(body2 == null && target2 == null, "a server with an untrusted certificate is refused (no fake server can be injected)");
}

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
