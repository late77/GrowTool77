// lang: C#, file: FishingFeature.cs, target: .NET 8 Windows, proj: GrowTool77
// Auto-fish state machine — based on live packet capture.
//
// CAST:  game sends GamePacket type=3, intData=3014, flags=16 → stored for replay
// BITE:  server sends OnPlayPositioned audio/splash.wav
//        BUT a cast also produces: splash → punch_locked → teleport in sequence.
//        Discriminator: if punch_locked.wav was seen < 800 ms ago → cast sound, skip.
//        If splash arrives without recent punch_locked → real bite, pull.
// PULL:  replay the stored cast packet byte-for-byte.
// DELAY: 77–300 ms random before each pull.
using System.Buffers.Binary;

namespace GrowTool77.Aura;

public sealed class FishingFeature
{
    // Growtopia item IDs for fishing rods. Add new rods here as needed.
    private static readonly HashSet<int> RodIds = new() { 3014 };
    public static bool IsFishingRod(int itemId) => RodIds.Contains(itemId);

    private enum FishState { Idle, WaitingBite, DelayingPull }

    private volatile bool _running;
    private volatile bool _logAll;
    private FishState     _state = FishState.Idle;

    private System.Threading.Timer? _pullTimer;
    private byte[]?  _castPacket;           // stored to replay as pull
    private DateTime _lastPunchLockedAt = DateTime.MinValue;

    private readonly Action<byte[]> _sendToServer;
    private readonly AuraLog        _log;

    public bool IsRunning      => _running;
    public bool LoggingEnabled { get => _logAll; set => _logAll = value; }

    public event Action<string>? StatusChanged;

    public FishingFeature(Action<byte[]> sendToServer, AuraLog log)
    {
        _sendToServer = sendToServer;
        _log          = log;
    }

    public void Start()
    {
        _running    = true;
        _state      = FishState.Idle;
        _castPacket = null;
        Fire("Active — cast your rod to begin");
        _log.Info("[Fish] Auto-fish started.");
    }

    public void Stop()
    {
        _running = false;
        _pullTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _pullTimer?.Dispose();
        _pullTimer  = null;
        _castPacket = null;
        _state      = FishState.Idle;
        Fire("Stopped");
        _log.Info("[Fish] Auto-fish stopped.");
    }

    // ── Called on the proxy network thread ───────────────────────────

    // Game sent type=3 with a known fishing rod ID → new cast, store packet.
    internal void OnFishingAction(byte[] rawPacket)
    {
        if (_state == FishState.DelayingPull) return; // our own pull bouncing back
        _castPacket = (byte[])rawPacket.Clone();
        _state      = FishState.WaitingBite;
        if (_logAll) _log.Info($"[Fish] Cast stored ({rawPacket.Length} bytes) — waiting for bite.");
        Fire("Cast detected — waiting for bite");
    }

    // All server → game CallFunctions that aren't handled elsewhere.
    internal void OnCallFunction(string name, VariantList vl)
    {
        if (_logAll)
        {
            var fields = string.Join(", ", vl.Items.Skip(1).Select((e, i) => e.Type switch
            {
                VariantList.TString => $"[{i+1}]S=\"{e.S}\"",
                VariantList.TInt    => $"[{i+1}]I={e.I}",
                VariantList.TUInt   => $"[{i+1}]U={e.U}",
                VariantList.TFloat  => $"[{i+1}]F={e.F1}",
                VariantList.TVec2   => $"[{i+1}]V2=({e.F1},{e.F2})",
                _                   => $"[{i+1}]?",
            }));
            _log.Info($"[Fish] server CallFunction: {name}  {fields}");
        }

        if (name != "OnPlayPositioned") return;
        if (vl.Items.Count < 2 || vl.Items[1].Type != VariantList.TString) return;
        OnPositionedSound(vl.Items[1].S);
    }

    // Server plain text packets (for logger only).
    internal void OnServerText(string text)
    {
        if (_logAll) _log.Info($"[Fish] server text: {Flatten(text)}");
    }

    // Game plain text packets (for logger only).
    internal void OnGameText(string text)
    {
        if (_logAll) _log.Info($"[Fish] game   text: {Flatten(text)}");
    }

    // ── Internal ─────────────────────────────────────────────────────

    private void OnPositionedSound(string sound)
    {
        // punch_locked.wav always follows a cast splash — update the timestamp.
        if (sound == "audio/punch_locked.wav")
        {
            _lastPunchLockedAt = DateTime.UtcNow;
            return;
        }

        if (sound != "audio/splash.wav") return;
        if (!_running || _state != FishState.WaitingBite || _castPacket == null) return;

        // Cast sound sequence: splash → punch_locked → teleport.
        // If punch_locked was seen recently this splash is part of that sequence → skip.
        if ((DateTime.UtcNow - _lastPunchLockedAt).TotalMilliseconds < 800) return;

        // Real bite.
        SchedulePull();
    }

    private void SchedulePull()
    {
        int delay = Random.Shared.Next(117, 218); // 117–217 ms inclusive
        _state = FishState.DelayingPull;
        Fire($"Bite! Pulling in {delay} ms");
        _log.Info($"[Fish] Bite detected — pulling in {delay} ms.");

        _pullTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _pullTimer?.Dispose();
        _pullTimer = new System.Threading.Timer(_ => SendPull(), null, delay, Timeout.Infinite);
    }

    private void SendPull()
    {
        if (!_running || _castPacket == null) return;

        var recastPacket = _castPacket; // hold ref before clearing
        _sendToServer(recastPacket);
        _castPacket = null;
        _state      = FishState.Idle;
        Fire("Pulled — recasting...");
        _log.Info("[Fish] Auto-pulled.");

        // Auto-recast after a short human-like pause.
        int recastDelay = Random.Shared.Next(400, 701); // 400–700 ms
        _pullTimer?.Dispose();
        _pullTimer = new System.Threading.Timer(_ => AutoRecast(recastPacket), null, recastDelay, Timeout.Infinite);
    }

    private void AutoRecast(byte[] packet)
    {
        if (!_running) return;
        _sendToServer(packet);
        _castPacket = packet; // keep for the next pull
        _state      = FishState.WaitingBite;
        Fire("Recast — waiting for bite");
        _log.Info("[Fish] Auto-recast.");
    }

    private void Fire(string status) => StatusChanged?.Invoke(status);

    private static string Flatten(string s) => s.Replace('\n', '|').Replace('\r', ' ').Trim();
}
