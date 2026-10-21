// lang: C#, file: AuraProxy.cs, target: .NET 8 Windows, proj: GrowTool77
// [AURA]->proxy: sits between the Growtopia game and the Growtopia server.
// Every packet is passed through unchanged except OnSendToServer, whose
// address is pointed back at [AURA] so the next sub-server also goes through
// it. Packets are only read for the world name and dropped items.
//
// v0.7 additions:
//   - SetFishingFeature: wires up FishingFeature to intercept fishing packets.
//   - EnqueueToServer: lets FishingFeature inject packets from any thread.
//   - InspectFromServer routes server plain text + unknown CallFunctions to fish.
//   - InspectFromGame routes all game plain text to fish.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GrowTool77.Aura;

public enum AuraState { Off, Waiting, Connecting, Connected }

public sealed record AuraStatus(AuraState State, string Detail);

public sealed class AuraProxy : IDisposable
{
    public const string Loopback = "127.0.0.1";

    private readonly AuraLog      _log;
    private readonly WorldTracker _world = new();
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly Queue<(byte Channel, byte[] Data)> _backlog = new();
    private readonly object _targetLock = new();
    private (string Ip, ushort Port)? _pendingTarget;

    private Thread? _thread;
    private volatile bool _running;

    private IntPtr _gameHost;
    private IntPtr _serverHost;
    private IntPtr _gamePeer;
    private IntPtr _serverPeer;
    private bool   _serverReady;

    private bool _worldDirty;
    private readonly Stopwatch _sincePublish = Stopwatch.StartNew();

    // Fish feature — set and read on the network thread via _inbox.
    private FishingFeature? _fishFeature;

    public ushort Port      { get; private set; }
    public bool   IsRunning => _running;
    public AuraStatus Status { get; private set; } = new(AuraState.Off, "[AURA] is off");

    public event Action<AuraStatus>? StatusChanged;
    public event Action<WorldView?>? WorldChanged;
    public event Action<IReadOnlyList<WorldVends>>? VendsChanged;

    private volatile float _playerX, _playerY;
    private volatile bool  _hasPlayer;

    public bool TryGetPlayer(out float x, out float y)
    {
        x = _playerX;
        y = _playerY;
        return _hasPlayer;
    }

    private volatile bool _debugDrops;
    public bool DebugDrops { get => _debugDrops; set => _debugDrops = value; }

    public AuraProxy(AuraLog log) => _log = log;

    // Wire a FishingFeature in or out. Thread-safe — applied on the network thread.
    public void SetFishingFeature(FishingFeature? feature) =>
        _inbox.Enqueue(() => _fishFeature = feature);

    // Send a raw packet to the server from any thread (used by FishingFeature).
    public void EnqueueToServer(byte[] data) => _inbox.Enqueue(() =>
    {
        if (_serverPeer != IntPtr.Zero && _serverReady)
            Send(_serverPeer, 0, data);
    });

    public void Start(ushort preferredPort = 16999)
    {
        if (_running) return;
        AuraNative.EnsureInitialized();

        for (int p = preferredPort; p < preferredPort + 20; p++)
        {
            _gameHost = AuraNative.aura_host_create(0, Loopback, (ushort)p, 8);
            if (_gameHost != IntPtr.Zero) { Port = (ushort)p; break; }
        }
        if (_gameHost == IntPtr.Zero)
            throw new InvalidOperationException("[AURA] could not open a local UDP port.");

        _serverHost = AuraNative.aura_host_create(1, null, 0, 4);
        if (_serverHost == IntPtr.Zero)
        {
            AuraNative.aura_host_destroy(_gameHost);
            _gameHost = IntPtr.Zero;
            throw new InvalidOperationException("[AURA] could not create the server connection.");
        }

        _running = true;
        _thread  = new Thread(Loop) { IsBackground = true, Name = "[AURA] relay" };
        _thread.Start();
        _log.Info($"[AURA]->proxy listening on {Loopback}:{Port}");
        SetStatus(AuraState.Waiting, "Waiting for Growtopia to connect");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(3000);
        _thread = null;
        SetStatus(AuraState.Off, "[AURA] is off");
        WorldChanged?.Invoke(null);
    }

    public void Dispose() => Stop();

    public void SetServerTarget(string ip, ushort port)
    {
        lock (_targetLock) _pendingTarget = (ip, port);
    }

    public void SetItemDatabase(ItemDatabase db) => _inbox.Enqueue(() =>
    {
        _world.Items = db;
        _worldDirty  = true;
    });

    public void RequestWorld() => _inbox.Enqueue(() => _worldDirty = true);

    // ── Network thread ──────────────────────────────────────────────

    private void Loop()
    {
        try
        {
            while (_running)
            {
                while (_inbox.TryDequeue(out var action)) action();
                Pump(_gameHost, fromGame: true);
                Pump(_serverHost, fromGame: false);

                if (_worldDirty && _sincePublish.ElapsedMilliseconds >= 250)
                {
                    _worldDirty = false;
                    _sincePublish.Restart();
                    WorldChanged?.Invoke(_world.Snapshot());
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[AURA] stopped unexpectedly: {ex.Message}");
            _running = false;
        }
        finally
        {
            if (_gamePeer   != IntPtr.Zero) AuraNative.aura_peer_disconnect_now(_gamePeer, 0);
            if (_serverPeer != IntPtr.Zero) AuraNative.aura_peer_disconnect_now(_serverPeer, 0);
            AuraNative.aura_host_flush(_gameHost);
            AuraNative.aura_host_flush(_serverHost);
            AuraNative.aura_host_destroy(_gameHost);
            AuraNative.aura_host_destroy(_serverHost);
            _gameHost = _serverHost = _gamePeer = _serverPeer = IntPtr.Zero;
            _serverReady = false;
            _backlog.Clear();
            _world.Leave();
        }
    }

    private void Pump(IntPtr host, bool fromGame)
    {
        uint timeout = 1;
        while (AuraNative.aura_host_service(host, timeout, out var ev) > 0)
        {
            timeout = 0;
            switch (ev.Type)
            {
                case AuraNative.EventConnect:
                    if (fromGame) OnGameConnected(ev.Peer); else OnServerConnected(ev.Peer);
                    break;
                case AuraNative.EventReceive:
                    var data = AuraNative.CopyPacket(ev);
                    AuraNative.aura_packet_destroy(ev.Packet);
                    if (fromGame) OnGamePacket(ev.Peer, ev.Channel, data);
                    else          OnServerPacket(ev.Peer, ev.Channel, data);
                    break;
                case AuraNative.EventDisconnect:
                    if (fromGame) OnGameDisconnected(ev.Peer); else OnServerDisconnected(ev.Peer);
                    break;
            }
        }
    }

    private void OnGameConnected(IntPtr peer)
    {
        if (_gamePeer != IntPtr.Zero && _gamePeer != peer)
            AuraNative.aura_peer_disconnect_now(_gamePeer, 0);
        _gamePeer = peer;
        AuraNative.aura_peer_timeout(peer, 0, 15000, 0);

        (string Ip, ushort Port)? target;
        lock (_targetLock)
        {
            target = _pendingTarget;
            _pendingTarget = null;
        }

        if (target == null)
        {
            _log.Warn("Growtopia connected, but [AURA] doesn't know the server yet. Close Growtopia and start it again while [AURA] is on.");
            AuraNative.aura_peer_disconnect(peer, 0);
            return;
        }

        if (_serverPeer != IntPtr.Zero) AuraNative.aura_peer_disconnect_now(_serverPeer, 0);
        _serverReady = false;
        _backlog.Clear();

        _serverPeer = AuraNative.aura_host_connect(_serverHost, target.Value.Ip, target.Value.Port);
        if (_serverPeer == IntPtr.Zero)
        {
            _log.Error($"Could not start a connection to {target.Value.Ip}:{target.Value.Port}.");
            AuraNative.aura_peer_disconnect(peer, 0);
            return;
        }

        _log.Info($"Growtopia connected. Connecting to server {target.Value.Ip}:{target.Value.Port}");
        SetStatus(AuraState.Connecting, $"Connecting to {target.Value.Ip}:{target.Value.Port}");
    }

    private void OnServerConnected(IntPtr peer)
    {
        if (peer != _serverPeer) return;
        _serverReady = true;
        while (_backlog.Count > 0)
        {
            var (channel, data) = _backlog.Dequeue();
            Send(_serverPeer, channel, data);
        }
        _log.Info("Connected to the Growtopia server.");
        SetStatus(AuraState.Connected, _world.CurrentWorld != null ? $"In world {_world.CurrentWorld}" : "Connected");
    }

    private void OnGameDisconnected(IntPtr peer)
    {
        if (peer != _gamePeer) return;
        _gamePeer = IntPtr.Zero;
        if (_serverPeer != IntPtr.Zero) AuraNative.aura_peer_disconnect(_serverPeer, 0);
        _world.Leave();
        _worldDirty = true;
        SetStatus(AuraState.Waiting, "Waiting for Growtopia to connect");
    }

    private void OnServerDisconnected(IntPtr peer)
    {
        if (peer != _serverPeer) return;
        _serverPeer  = IntPtr.Zero;
        _serverReady = false;
        if (_gamePeer != IntPtr.Zero) AuraNative.aura_peer_disconnect_later(_gamePeer, 0);
        SetStatus(AuraState.Waiting, "Waiting for Growtopia to connect");
    }

    private void OnGamePacket(IntPtr peer, byte channel, byte[] data)
    {
        if (peer != _gamePeer) return;
        InspectFromGame(data);

        if (_serverReady && _serverPeer != IntPtr.Zero) Send(_serverPeer, channel, data);
        else if (_backlog.Count < 512) _backlog.Enqueue((channel, data));
    }

    private void OnServerPacket(IntPtr peer, byte channel, byte[] data)
    {
        if (peer != _serverPeer) return;
        data = InspectFromServer(data);
        if (_gamePeer != IntPtr.Zero) Send(_gamePeer, channel, data);
    }

    private void Send(IntPtr peer, byte channel, byte[] data)
    {
        if (AuraNative.aura_peer_send(peer, channel, data, (uint)data.Length, AuraNative.PacketReliable) != 0)
            _log.Warn($"A packet of {data.Length} bytes could not be forwarded.");
    }

    // ── Packet inspection ───────────────────────────────────────────

    // Only world actions are looked at. Login text is never stored or logged.
    private void InspectFromGame(byte[] data)
    {
        if (GameUpdate.TryRead(data, out var gu))
        {
            // Log all game→server GamePackets except type 0 (movement, too frequent)
            // and type 11 (pickup, handled separately). Used to find the PULL packet.
            if (_fishFeature is { LoggingEnabled: true } && gu.Type != 0 && gu.Type != 11)
                _log.Info($"[Fish] game   GamePacket type={gu.Type} netId={gu.NetId} intData={gu.IntData} pos=({gu.PosX:0},{gu.PosY:0}) flags={gu.Flags} intX={gu.IntX} intY={gu.IntY}");

            // type=3 (PACKET_SEND_TILE_UPDATE_REQUEST) with a known fishing rod ID = cast or pull.
            // FishingFeature stores the packet bytes to replay as the auto-pull.
            if (gu.Type == 3 && _fishFeature != null && FishingFeature.IsFishingRod(gu.IntData))
                _fishFeature.OnFishingAction(data);

            // PACKET_ITEM_ACTIVATE_OBJECT_REQUEST (11): the game asks to pick up a drop.
            if (gu.Type == 11 && _world.CurrentWorld != null)
            {
                var result = _world.NotePickupRequest(gu.IntData, gu.PosX, gu.PosY);
                if (_debugDrops)
                    _log.Info($"Pickup request from the game: id={gu.IntData} pos=({gu.PosX:0},{gu.PosY:0}) => {result}");
                return;
            }

            // PACKET_STATE (0): the player's own movement, sent many times a second.
            if (gu.Type == 0 && _world.CurrentWorld != null && float.IsFinite(gu.PosX) && float.IsFinite(gu.PosY))
            {
                _world.PlayerX  = gu.PosX;
                _world.PlayerY  = gu.PosY;
                _world.OwnNetId = gu.NetId;
                _playerX   = gu.PosX;
                _playerY   = gu.PosY;
                _hasPlayer = true;
            }
            return;
        }

        if (!TextMessage.TryRead(data, out var text)) return;

        // Fish feature sees all game→server text (logger + pull detection).
        _fishFeature?.OnGameText(text);

        if (!text.StartsWith("action|", StringComparison.Ordinal)) return;

        if (text.StartsWith("action|join_request", StringComparison.Ordinal))
        {
            var name = TextMessage.Value(text, "name");
            _log.Info($"Joining world {name?.ToUpperInvariant() ?? "?"}");
            _hasPlayer = false;
            _world.Leave();
            _worldDirty = true;
        }
        else if (text.StartsWith("action|quit_to_exit", StringComparison.Ordinal))
        {
            _log.Info("Left the world.");
            _hasPlayer = false;
            _world.Leave();
            _worldDirty = true;
            SetStatus(AuraState.Connected, "Connected");
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CollectedLine =
        new(@"^Collected (\d+) (.+?)\.(?:\sRarity: \d+)?\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private void HandleConsoleMessage(VariantList vl)
    {
        if (_world.CurrentWorld == null || vl.Items.Count < 2 || vl.Items[1].S is not string raw) return;
        var text = StripColors(raw);
        if (!text.StartsWith("Collected ", StringComparison.Ordinal)) return;

        var m = CollectedLine.Match(text);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int amount)) return;
        var item = _world.Items?.FindByName(m.Groups[2].Value);
        if (item == null) return;

        var result = _world.NoteCollected(amount, item.Id);
        if (result is "removed" || result.StartsWith("reduced", StringComparison.Ordinal)) _worldDirty = true;
        if (_debugDrops) _log.Info($"Server says: collected {amount} x {item.Name} => {result}");
    }

    internal static string StripColors(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '`') { i++; continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private byte[] InspectFromServer(byte[] data)
    {
        // Server plain text (NetMessage 2 or 3) — route to fish feature for logging.
        // These are NOT game packets (type 4) so they fall through GameUpdate.TryRead.
        if (TextMessage.TryRead(data, out var serverText))
        {
            _fishFeature?.OnServerText(serverText);
            return data;
        }

        if (!GameUpdate.TryRead(data, out var gu)) return data;

        switch (gu.Type)
        {
            case GamePacketType.CallFunction:
                if (!VariantList.TryParse(data, GameUpdate.ExtraStart, gu.ExtraLength, out var vl)) break;
                switch (vl.Function)
                {
                    case "OnSendToServer":
                        return RewriteSendToServer(data, gu, vl);
                    case "OnConsoleMessage":
                        HandleConsoleMessage(vl);
                        break;
                    case "OnRequestWorldSelectMenu":
                        _world.Leave();
                        _worldDirty = true;
                        SetStatus(AuraState.Connected, "Connected");
                        break;
                    default:
                        // Route all other CallFunctions to fish (logger + bite detection).
                        _fishFeature?.OnCallFunction(vl.Function ?? "", vl);
                        break;
                }
                break;

            case GamePacketType.SendMapData:
                try
                {
                    var snapshot = WorldParser.Parse(data, GameUpdate.ExtraStart, gu.ExtraLength, _world.Items);
                    _world.Enter(snapshot);
                    _worldDirty = true;
                    VendsChanged?.Invoke(_world.VendSnapshot());
                    _log.Info($"Entered world {snapshot.Name}: {snapshot.Objects.Count} dropped stacks, {snapshot.Vends.Count} vending machines, last drop id {snapshot.LastObjectUid}" +
                              (snapshot.TilesParsed ? "" : $" (fallback scan: {snapshot.Note})"));
                    SetStatus(AuraState.Connected, $"In world {snapshot.Name}");
                }
                catch (FormatException ex)
                {
                    _log.Warn($"World data could not be read: {ex.Message}");
                }
                break;

            case GamePacketType.ItemChangeObject:
            {
                uint target   = unchecked((uint)gu.ItemNetId);
                bool knownBef = _world.HasObject(target);
                uint lastBef  = _world.LastUid;
                bool applied  = _world.Apply(gu);
                if (applied) _worldDirty = true;

                if (_debugDrops)
                {
                    string kind = gu.NetId switch { -1 => "drop", -3 => "change", _ => $"pickup by net id {gu.NetId}" };
                    _log.Info($"Drop packet [{kind}] id={target} item={gu.IntData} ({_world.Items?.NameOf(gu.IntData) ?? "?"}) " +
                              $"amount(float)={gu.FloatVar:0.##} amount(int)={gu.RawAt16} pos=({gu.PosX:0},{gu.PosY:0}) " +
                              $"known before={knownBef} last id {lastBef}->{_world.LastUid} objects={_world.ObjectCount} resyncs={_world.ResyncCount} " +
                              $"=> {(applied ? "applied" : "NO MATCH")}");
                }
                break;
            }

            case GamePacketType.SendItemDatabaseData:
                try
                {
                    var db = ItemDatabase.ParseCompressed(data, GameUpdate.ExtraStart, gu.ExtraLength);
                    _world.Items = db;
                    _worldDirty  = true;
                    _log.Info($"Item names loaded from the server ({db.Count} items, v{db.Version}).");
                }
                catch (Exception ex) when (ex is FormatException or InvalidDataException)
                {
                    _log.Warn($"Item list from the server could not be read: {ex.Message}");
                }
                break;
        }
        return data;
    }

    private byte[] RewriteSendToServer(byte[] data, in GameUpdate gu, VariantList vl)
    {
        int? port = vl.GetInt(1);
        if (vl.Items.Count < 5 || vl.Items[4].Type != VariantList.TString || port is null or <= 0 or > 65535)
        {
            _log.Warn("Server switch had an unexpected format; it was passed through without [AURA].");
            return data;
        }

        string address = vl.Items[4].S;
        int bar = address.IndexOf('|');
        string host = bar < 0 ? address : address[..bar];
        string? ip = ResolveIPv4(host);
        if (ip == null)
        {
            _log.Warn($"Server switch to '{host}' could not be resolved; passed through without [AURA].");
            return data;
        }

        SetServerTarget(ip, (ushort)port.Value);
        vl.SetInt(1, Port);
        vl.Items[4].S = Loopback + (bar < 0 ? "" : address[bar..]);
        _log.Info($"Server switch to {ip}:{port} kept inside [AURA].");
        return VariantList.RebuildCallFunction(data, gu, vl.Serialize());
    }

    private static string? ResolveIPv4(string host)
    {
        if (IPAddress.TryParse(host, out var parsed))
            return parsed.AddressFamily == AddressFamily.InterNetwork ? parsed.ToString() : null;
        try
        {
            return Dns.GetHostAddresses(host)
                      .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                      ?.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private void SetStatus(AuraState state, string detail)
    {
        Status = new AuraStatus(state, detail);
        StatusChanged?.Invoke(Status);
    }
}
