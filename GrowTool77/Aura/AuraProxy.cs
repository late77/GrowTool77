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
    private FishingFeature?    _fishFeature;
    private SurgeryFeature?    _surgFeature;

    public ushort Port      { get; private set; }
    public bool   IsRunning => _running;
    public AuraStatus Status { get; private set; } = new(AuraState.Off, "[AURA] is off");

    public event Action<AuraStatus>? StatusChanged;
    public event Action<WorldView?>? WorldChanged;
    public event Action<IReadOnlyList<WorldVends>>? VendsChanged;

    private volatile float _playerX, _playerY;
    private volatile bool  _hasPlayer;
    private volatile string? _localPlayerName;

    public string?  LocalPlayerName => _localPlayerName;
    public event Action<string>? PlayerNameChanged;

    public bool TryGetPlayer(out float x, out float y)
    {
        x = _playerX;
        y = _playerY;
        return _hasPlayer;
    }

    private volatile bool _debugDrops;
    private volatile bool _verboseLog;

    // When on: log every packet in both directions (truncated).
    // Use this to discover dialog names, button IDs, and unknown packet types.
    public bool VerboseLog
    {
        get => _verboseLog;
        set { _verboseLog = value; _log.Info($"[VERBOSE] Packet logging {(value ? "ON" : "OFF")} — all packets will appear in [AURA] log."); }
    }

    private FarmFeature?      _farmFeature;
    private ProvidersFeature? _providersFeature;
    private AutoBuyFeature?   _autoBuyFeature;
    private ProxyCommands?    _proxyCommands;

    public void SetFarmFeature(FarmFeature? f)           => _inbox.Enqueue(() => _farmFeature      = f);
    public void SetProvidersFeature(ProvidersFeature? p) => _inbox.Enqueue(() => _providersFeature = p);
    public void SetAutoBuyFeature(AutoBuyFeature? b)     => _inbox.Enqueue(() => _autoBuyFeature   = b);
    public void SetProxyCommands(ProxyCommands? c)       => _inbox.Enqueue(() => _proxyCommands    = c);
    public bool DebugDrops { get => _debugDrops; set => _debugDrops = value; }

    public AuraProxy(AuraLog log) => _log = log;

    // Wire a FishingFeature in or out. Thread-safe — applied on the network thread.
    public void SetFishingFeature(FishingFeature? feature) =>
        _inbox.Enqueue(() => _fishFeature = feature);

    // Wire a SurgeryFeature in or out.
    public void SetSurgeryFeature(SurgeryFeature? feature) =>
        _inbox.Enqueue(() => _surgFeature = feature);

    // QoL: skip gazette (login news dialog)
    public bool SkipGazette { get; set; }
    public event Action? GazetteArrived; // fires on UI thread when gazette is detected

    // Set by WrenchTile, cleared immediately when the first type=0 is intercepted.
    private (int X, int Y)? _pendingDoorFetch;
    // Holds the door tile coords while we wait for the dialog response.
    private (int X, int Y)? _capturedDoorTile;

    // Fired on the UI thread when the server returns a door's own ID.
    public event Action<int, int, string>? DoorIdCaptured; // (tileX, tileY, ownId)

    /// <summary>
    /// Sends a wrench (type=3 with wrench item ID=32) to a tile via the proxy.
    /// The server responds with OnDialogRequest if the player has permission.
    /// The door's own ID is parsed from the response and fired via DoorIdCaptured.
    /// </summary>
    /// <summary>
    /// Requests a door's own ID. Sets _pendingDoorFetch so that the next type=0
    /// position packet from the game is intercepted in InspectFromGame, its
    /// PosX/PosY spoofed to the door tile, and a wrench (type=3) is queued
    /// immediately after. Server sees player at the door and returns the dialog.
    /// </summary>
    public void WrenchTile(int tileX, int tileY) =>
        _inbox.Enqueue(() => _pendingDoorFetch = (tileX, tileY));

    // Send a raw packet to the server from any thread (used by FishingFeature).
    public void EnqueueToServer(byte[] data) => _inbox.Enqueue(() =>
    {
        if (_serverPeer != IntPtr.Zero && _serverReady)
            Send(_serverPeer, 0, data);
    });

    // Send a packet to the game client (appears as if from server — used by ProxyCommands).
    public void SendToGameClient(byte[] data) => _inbox.Enqueue(() =>
    {
        if (_gamePeer != IntPtr.Zero) Send(_gamePeer, 0, data);
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
            _log.Warn("If you're on home WiFi and this works on mobile data, your router or ISP " +
                       "may be blocking UDP on Growtopia's game server ports (17000-17200). " +
                       "Try: router settings → open UDP 17000-17200 outbound, or use mobile hotspot.");
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
        var toSend = InspectFromGame(data) ?? data;  // null = forward unchanged

        if (toSend.Length == 0) return; // Array.Empty = explicitly dropped, send nothing

        if (_serverReady && _serverPeer != IntPtr.Zero) Send(_serverPeer, channel, toSend);
        else if (_backlog.Count < 512) _backlog.Enqueue((channel, toSend));
    }

    private void OnServerPacket(IntPtr peer, byte channel, byte[] data)
    {
        if (peer != _serverPeer) return;
        var toSend = InspectFromServer(data);
        if (toSend.Length == 0) return;           // packet suppressed (e.g. gazette skip)
        if (_gamePeer != IntPtr.Zero) Send(_gamePeer, channel, toSend);
    }

    private void Send(IntPtr peer, byte channel, byte[] data)
    {
        if (AuraNative.aura_peer_send(peer, channel, data, (uint)data.Length, AuraNative.PacketReliable) != 0)
            _log.Warn($"A packet of {data.Length} bytes could not be forwarded.");
    }

    // ── Packet inspection ───────────────────────────────────────────

    // Only world actions are looked at. Login text is never stored or logged.
    // Returns a modified packet to forward, or null to forward original unchanged.
    private byte[]? InspectFromGame(byte[] data)
    {
        if (GameUpdate.TryRead(data, out var gu))
        {
            if (_verboseLog && gu.Type != 0)
                _log.Info($"[PKT→SRV] GamePkt type={gu.Type} intData={gu.IntData} flags={gu.Flags} intX={gu.IntX} intY={gu.IntY} pos=({gu.PosX:0},{gu.PosY:0})");

            // Log all game→server GamePackets except type 0 (movement, too frequent)
            // and type 11 (pickup, handled separately). Used to find the PULL packet.
            if (_fishFeature is { LoggingEnabled: true } && gu.Type != 0 && gu.Type != 11)
                _log.Info($"[Fish] game   GamePacket type={gu.Type} netId={gu.NetId} intData={gu.IntData} pos=({gu.PosX:0},{gu.PosY:0}) flags={gu.Flags} intX={gu.IntX} intY={gu.IntY}");

            // ── Door ID fetch: intercept the next type=0 packet and spoof position ──
            // This is the most reliable proximity bypass: we use the game's own
            // position packet (correct NetId, flags, velocity) and only change PosX/PosY
            // to place the player at the door tile. The server updates its position
            // tracking, then we immediately send the wrench packet after.
            if (gu.Type == 0 && _pendingDoorFetch.HasValue)
            {
                var (wx, wy) = _pendingDoorFetch.Value;
                _pendingDoorFetch = null; // ← clear immediately so only ONE packet is spoofed

                const float TP = 32f;
                float spx = wx * TP + 10f;
                float spy = wy * TP;
                int   nid = gu.NetId;

                var mod = (byte[])data.Clone();
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(mod.AsSpan(4 + 24), spx);
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(mod.AsSpan(4 + 28), spy);

                // Store coords for the dialog capture BEFORE clearing pendingDoorFetch.
                _capturedDoorTile = (wx, wy);

                // Enqueue wrench — processed on the same thread after this packet is sent
                int cx = wx, cy = wy;
                _inbox.Enqueue(() =>
                {
                    if (_serverPeer == IntPtr.Zero || !_serverReady) return;
                    float px2 = cx * TP + 10f, py2 = cy * TP;
                    var wrench = new byte[4 + 56];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wrench, NetMessage.GamePacket);
                    wrench[4] = 3;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wrench.AsSpan(4 + 4),  nid);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wrench.AsSpan(4 + 12), 0x200);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wrench.AsSpan(4 + 20), 32);
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(wrench.AsSpan(4 + 24), px2);
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(wrench.AsSpan(4 + 28), py2);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wrench.AsSpan(4 + 44), cx);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wrench.AsSpan(4 + 48), cy);
                    Send(_serverPeer, 0, wrench);
                });

                return mod;
            }

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
                return null;
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
            return null;
        }

        if (!TextMessage.TryRead(data, out var text)) return null;

        if (_verboseLog)
            _log.Info($"[PKT→SRV] Text: {text.Replace('\n','|').Substring(0, Math.Min(300, text.Length))}");

        // Auto Buy: intercept shop buy clicks
        if (_autoBuyFeature != null && text.StartsWith("action|buy|item|", StringComparison.Ordinal))
            _autoBuyFeature.OnGameBuyAction(text);

        // Both features see all game→server text.
        _fishFeature?.OnGameText(text);
        _surgFeature?.OnGameText(text);

        // Proxy commands intercept /pdest /pid /cldd before they reach server.
        // Return Array.Empty to signal "drop this packet" (null = forward unchanged).
        if (_proxyCommands != null && _proxyCommands.OnGameText(text))
            return Array.Empty<byte>();

        // Capture GrowID from the login packet.
        if (text.StartsWith("tankIDName|", StringComparison.Ordinal))
        {
            var growId = TextMessage.Value(text, "tankIDName");
            if (!string.IsNullOrEmpty(growId) && growId != _localPlayerName)
            {
                _localPlayerName = growId;
                PlayerNameChanged?.Invoke(growId);
            }
        }

        if (!text.StartsWith("action|", StringComparison.Ordinal)) return null;

        if (text.StartsWith("action|join_request", StringComparison.Ordinal))
        {
            var name = TextMessage.Value(text, "name");
            _log.Info($"Joining world {name?.ToUpperInvariant() ?? "?"}");
            _hasPlayer = false;
            _world.Leave();
            _worldDirty = true;
            _surgFeature?.OnWorldChanged();
        }
        else if (text.StartsWith("action|quit_to_exit", StringComparison.Ordinal))
        {
            _log.Info("Left the world.");
            _hasPlayer = false;
            _world.Leave();
            _worldDirty = true;
            _surgFeature?.OnWorldChanged();
            _farmFeature?.OnWorldChanged();
            SetStatus(AuraState.Connected, "Connected");
        }
        return null;
    }

    // Called after world view is published — update ProvidersFeature with new Fg tile data
    public void NotifyProvidersWorldChanged(WorldView? world) =>
        _inbox.Enqueue(() => _providersFeature?.OnWorldChanged(world));

    private static readonly System.Text.RegularExpressions.Regex CollectedLine =
        new(@"^Collected (\d+) (.+?)\.(?:\sRarity: \d+)?\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private void HandleConsoleMessage(VariantList vl)
    {
        if (vl.Items.Count < 2 || vl.Items[1].S is not string raw) return;
        // Forward to AutoBuy for inventory-full detection
        _autoBuyFeature?.OnServerMessage(raw);
        if (_world.CurrentWorld == null) return;
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
        // Server plain text (NetMessage 2 or 3) — route to features for logging.
        // These are NOT game packets (type 4) so they fall through GameUpdate.TryRead.
        if (TextMessage.TryRead(data, out var serverText))
        {
            if (_verboseLog)
                _log.Info($"[SRV→PKT] Text: {serverText.Replace('\n','|').Substring(0, Math.Min(300, serverText.Length))}");
            _fishFeature?.OnServerText(serverText);
            _surgFeature?.OnServerText(serverText);
            return data;
        }

        if (!GameUpdate.TryRead(data, out var gu)) return data;

        if (_verboseLog && gu.Type != 0) // skip position spam
            _log.Info($"[SRV→PKT] GamePkt type={gu.Type} netId={gu.NetId} intData={gu.IntData} flags={gu.Flags}");

        switch (gu.Type)
        {
            case GamePacketType.CallFunction:
                if (!VariantList.TryParse(data, GameUpdate.ExtraStart, gu.ExtraLength, out var vl)) break;
                if (_verboseLog)
                {
                    string vlBody = vl.Items.Count > 1 && vl.Items[1].Type == VariantList.TString
                        ? vl.Items[1].S.Replace('\n','|').Substring(0, Math.Min(400, vl.Items[1].S.Length))
                        : "";
                    _log.Info($"[SRV→PKT] CallFn={vl.Function} body={vlBody}");
                }
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
                        // Route to features first — Insta auto-action + logging.
                        _fishFeature?.OnCallFunction(vl.Function ?? "", vl);
                        _surgFeature?.OnCallFunction(vl.Function ?? "", vl);

                        // Manual surgery highlight: modify the dialog AFTER the feature
                        // has processed it, then rebuild the packet sent to the game.
                        if (vl.Function == "OnDialogRequest"
                            && _surgFeature != null
                            && vl.Items.Count > 1
                            && vl.Items[1].Type == VariantList.TString)
                        {
                            string? modified = _surgFeature.ModifyDialogForManual(vl.Items[1].S);
                            if (modified != null)
                            {
                                vl.Items[1].S = modified;
                                return VariantList.RebuildCallFunction(data, gu, vl.Serialize());
                            }
                        }

                        // Auto Buy: shop purchase confirmation
                        if (vl.Function == "OnStorePurchaseResult" && _autoBuyFeature != null
                            && vl.Items.Count > 1 && vl.Items[1].Type == VariantList.TString)
                            _autoBuyFeature.OnStorePurchaseResult(vl.Items[1].S);


                        // QoL: auto-dismiss the Gazette (login news dialog)
                        if (vl.Function == "OnDialogRequest"
                            && vl.Items.Count > 1
                            && vl.Items[1].Type == VariantList.TString
                            && vl.Items[1].S.Contains("end_dialog|gazette", StringComparison.Ordinal))
                        {
                            GazetteArrived?.Invoke();
                            if (SkipGazette)
                            {
                                // Send dismiss to server then DROP the dialog — game never sees it.
                                // Array.Empty = "suppress this packet" signal in OnServerPacket.
                                EnqueueToServer(BuildTextPacket(
                                    "action|dialog_return\ndialog_name|gazette\nbuttonClicked|banner|\n"));
                                return Array.Empty<byte>();
                            }
                        }

                        // ProxyCommands: pre-fill door destination or ID fields (/pdest /pid)
                        if (vl.Function == "OnDialogRequest"
                            && _proxyCommands != null
                            && vl.Items.Count > 1
                            && vl.Items[1].Type == VariantList.TString
                            && vl.Items[1].S.Contains("Door", StringComparison.OrdinalIgnoreCase))
                        {
                            string? filled = _proxyCommands.OnDoorDialog(vl.Items[1].S);
                            if (filled != null)
                            {
                                vl.Items[1].S = filled;
                                return VariantList.RebuildCallFunction(data, gu, vl.Serialize());
                            }
                        }

                        // Capture door own ID from wrench response.
                        if (vl.Function == "OnDialogRequest" && _capturedDoorTile.HasValue
                            && vl.Items.Count > 1 && vl.Items[1].Type == VariantList.TString)
                        {
                            string dlg = vl.Items[1].S;
                            if (dlg.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                                && dlg.Contains("Door", StringComparison.OrdinalIgnoreCase))
                            {
                                string ownId = ParseDoorOwnId(dlg);
                                if (!string.IsNullOrEmpty(ownId))
                                {
                                    var (tx, ty) = _capturedDoorTile.Value;
                                    _world.SetDoorId(tx, ty, ownId);
                                    _worldDirty = true;
                                    var captured = DoorIdCaptured;
                                    if (captured != null)
                                    {
                                        int cx = tx, cy = ty;
                                        Task.Run(() => captured(cx, cy, ownId));
                                    }
                                    _log.Info($"[Doors] ID found for door at ({tx},{ty}): {ownId}");
                                }
                                _capturedDoorTile = null;
                            }
                        }
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
                // Notify FarmFeature of new drops for break detection
                if (gu.NetId == -1 && _farmFeature != null)
                    _farmFeature.OnItemDropped(gu.PosX, gu.PosY);

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

    // Parse the door's own ID from an "Edit Door" OnDialogRequest dialog string.
    // Growtopia sends: add_text_input|id|<value>|<maxlen>|
    private static byte[] BuildTextPacket(string text)
    {
        var body   = System.Text.Encoding.Latin1.GetBytes(text + "\0");
        var packet = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet, NetMessage.GenericText);
        Buffer.BlockCopy(body, 0, packet, 4, body.Length);
        return packet;
    }

    private static string ParseDoorOwnId(string dialog)
    {
        // Try add_text_input format: add_text_input|id|FAF|24|
        var m = System.Text.RegularExpressions.Regex.Match(
            dialog, @"add_text_input\|id\|([^|]*)\|", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Trim();

        // Try embed_data format: embed_data|id|FAF|
        m = System.Text.RegularExpressions.Regex.Match(
            dialog, @"embed_data\|id\|([^|]*)\|", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Trim();

        return "";
    }

    private void SetStatus(AuraState state, string detail)
    {
        Status = new AuraStatus(state, detail);
        StatusChanged?.Invoke(Status);
    }
}
