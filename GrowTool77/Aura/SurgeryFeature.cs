// lang: C#, file: SurgeryFeature.cs, target: .NET 8 Windows, proj: GrowTool77
// Surgery feature — Full Auto and Manual modes.
// Decision engine based on the authoritative GT surgery guide (growtopiagame forums):
//
// CORRECT PRIORITY (universal for all diagnoses):
//  0. Pre-surgery (surge dialog) → auto-confirm
//  1. DEFIB          — heart stopped (2 moves or die)
//  2. SPONGE         — can't see (ONLY option at this point)
//  3. ANESTHETIC     — Coming to + any incisions OR Coming to alone
//  4. ANESTHETIC     — Awake + incisions open
//  5. STITCHES       — losing blood quickly / very quickly (stop bleed FIRST)
//  6. TRANSFUSION    — pulse extremely weak
//  7. LAB KIT        — fever climbing AND antibiotics not yet unlocked
//  8. ANTIBIOTICS    — fever climbing / temp > 100
//  9. ULTRASOUND     — diagnose (if not yet done; Splint only appears after)
// 10. Flu check      — if antibiotics only (no Fix It ever): just spam Antibiotics
// 11. SPLINT         — fractures (appears after Ultrasound)
// 12. ANESTHETIC     — sedate before any incision (Scalpel gated on Unconscious)
// 13. SCALPEL        — incisions (game auto-locks when enough done)
// 14. FIX IT         — ONLY when patient is UNCONSCIOUS
// 15. STITCHES       — close all incisions
// 16. TRANSFUSION    — pulse weak (last resort)
// NOTE: Antiseptic is NOT required for any surgery. Skip it.
// NOTE: Sponge is ONLY correct when "can't see" — do NOT use it proactively.
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace GrowTool77.Aura;

public enum SurgMode { Manual, Insta }

public sealed class SurgeryFeature
{
    // ── Tool IDs ─────────────────────────────────────────────────────
    private const int Defib       = 4312;
    private const int Sponge      = 1258;
    private const int Anesthetic  = 1262;
    private const int Stitches    = 1270;
    private const int Scalpel     = 1260;
    private const int Ultrasound  = 4316;
    private const int Antiseptic  = 1264;  // not required; never chosen
    private const int LabKit      = 4318;
    private const int Antibiotics = 1266;
    private const int Transfusion = 4310;
    private const int Splint      = 1268;
    private const int Pins        = 4308;
    private const int Clamp       = 4314;
    private const int FixIt       = 1296;

    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [Defib]       = "Defibrillator", [Sponge]      = "Sponge",
        [Anesthetic]  = "Anesthetic",    [Stitches]     = "Stitches",
        [Scalpel]     = "Scalpel",       [Ultrasound]   = "Ultrasound",
        [Antiseptic]  = "Antiseptic",    [LabKit]       = "Lab Kit",
        [Antibiotics] = "Antibiotics",   [Transfusion]  = "Transfusion",
        [Splint]      = "Splint",        [Pins]         = "Pins",
        [Clamp]       = "Clamp",         [FixIt]        = "Fix It!",
    };

    // Surgery dialog slot → overlay highlight position
    // Row 0: Defib(0) Sponge(1) Anesthetic(2) Stitches(3) Scalpel(4)
    // Row 1: Ultrasound(5) Antiseptic(6) FixIt(7) LabKit(8) Antibiotics(9)
    // Row 2: Transfusion(10) Splint(11) Pins(12) Clamp(13) -(14)
    private static readonly IReadOnlyDictionary<int, int> ToolSlot = new Dictionary<int, int>
    {
        [Defib]=0, [Sponge]=1, [Anesthetic]=2, [Stitches]=3, [Scalpel]=4,
        [Ultrasound]=5, [Antiseptic]=6, [FixIt]=7, [LabKit]=8, [Antibiotics]=9,
        [Transfusion]=10, [Splint]=11, [Pins]=12, [Clamp]=13,
    };

    // ── Parsed dialog state ───────────────────────────────────────────
    private readonly struct SurgState
    {
        public readonly bool PreSurgery;
        public readonly int  TileX, TileY;
        // Status
        public readonly bool Awake, ComingTo, Unconscious;
        public readonly bool HeartStopped;
        public readonly bool PulseWeak, PulseExtremelyWeak;
        // Visibility
        public readonly bool CantSee;
        // Temp
        public readonly float Temp;
        public readonly bool FeverClimbing;
        // Site (not used for decisions; antiseptic not required)
        // Incisions
        public readonly int Incisions;
        // Bleeding
        public readonly bool BleedQuickly, BleedVeryQuickly;
        public bool BleedingFast => BleedQuickly || BleedVeryQuickly;
        // Fractures / shattered
        public readonly bool HasFractures, HasShattered;
        // Diagnosis flags
        public readonly bool Diagnosed;
        public readonly bool IsFlu;
        public readonly bool FixItAvailable;
        public readonly bool AntibioticsUnlocked;
        // Available tools
        public readonly HashSet<int> Tools;
        public bool Has(int id) => Tools.Contains(id);

        public SurgState(bool pre, int tx, int ty,
                         bool awake, bool comingTo, bool unconscious,
                         bool heartStop, bool pulseWeak, bool pulseExWeak,
                         bool cantSee, float temp, bool feverClimbing,
                         int incisions, bool bleedQ, bool bleedVQ,
                         bool hasFrac, bool hasShattered,
                         bool diagnosed, bool isFlu,
                         bool fixIt, bool antibiotics,
                         HashSet<int> tools)
        {
            PreSurgery         = pre;      TileX = tx;     TileY = ty;
            Awake              = awake;    ComingTo = comingTo; Unconscious = unconscious;
            HeartStopped       = heartStop;
            PulseWeak          = pulseWeak; PulseExtremelyWeak = pulseExWeak;
            CantSee            = cantSee;
            Temp               = temp;     FeverClimbing = feverClimbing;
            Incisions          = incisions;
            BleedQuickly       = bleedQ;   BleedVeryQuickly = bleedVQ;
            HasFractures       = hasFrac;  HasShattered = hasShattered;
            Diagnosed          = diagnosed; IsFlu = isFlu;
            FixItAvailable     = fixIt;    AntibioticsUnlocked = antibiotics;
            Tools              = tools;
        }
    }

    // ── Fields ────────────────────────────────────────────────────────
    private volatile bool _running;
    private volatile bool _logAll;
    public  SurgMode Mode { get; set; } = SurgMode.Manual;

    private System.Threading.Timer? _actionTimer;
    private readonly Action<byte[]> _sendToServer;
    private readonly AuraLog        _log;

    public bool IsRunning      => _running;
    public bool LoggingEnabled { get => _logAll; set => _logAll = value; }

    public event Action<string>? StatusChanged;
    public event Action<string>? ToolRecommended; // tool name for status label
    public event Action<int>?    ToolSlotChanged;  // overlay slot index (0-14, -1=none)

    public SurgeryFeature(Action<byte[]> sendToServer, AuraLog log)
    {
        _sendToServer = sendToServer;
        _log          = log;
    }

    public void Start()
    {
        _running = true;
        Fire("Active — wrench a Surg-E or patient");
        _log.Info($"[Surg] Started ({Mode} mode).");
    }

    public void Stop()
    {
        _running = false;
        _actionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _actionTimer?.Dispose();
        _actionTimer = null;
        ToolSlotChanged?.Invoke(-1);
        Fire("Stopped");
    }

    internal void OnWorldChanged()
    {
        _actionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        ToolSlotChanged?.Invoke(-1);
        if (_running) Fire("World changed — ready");
    }

    // ── Proxy callbacks ───────────────────────────────────────────────

    internal void OnCallFunction(string name, VariantList vl)
    {
        if (name != "OnDialogRequest") return;
        if (vl.Items.Count < 2 || vl.Items[1].Type != VariantList.TString) return;

        string d = vl.Items[1].S;
        bool isSurg = d.Contains("add_popup_name|Surgery",        StringComparison.Ordinal)
                   || d.Contains("add_popup_name|SurgeryPrePopup", StringComparison.Ordinal);
        if (!isSurg) return;

        if (_logAll) LogDialog(d);
        if (!_running) return;

        var state = ParseDialog(d);
        if (Mode == SurgMode.Insta) ScheduleAction(state);
        else                         RecommendTool(state);
    }

    internal void OnServerText(string t) { if (_logAll && _running) _log.Info($"[Surg] s: {Flatten(t)}"); }
    internal void OnGameText(string t)
    {
        if (!_logAll) return;
        if (t.StartsWith("action|dialog_return", StringComparison.Ordinal))
            _log.Info($"[Surg] <<< {Flatten(t)}");
    }

    // ── Manual: modify dialog for in-game [→] highlight ──────────────

    internal string? ModifyDialogForManual(string dialog)
    {
        if (Mode != SurgMode.Manual || !_running) return null;
        var s = ParseDialog(dialog);
        int tool = Decide(s);
        if (tool == 0) return null;

        // Add [→] prefix in red to the recommended tool's button label
        string tag    = $"|tool{tool}|`$";
        string tagged = $"|tool{tool}|`4[>] `$"; // [>] is Latin-1 safe (→ renders as ? in GT)
        if (!dialog.Contains(tag, StringComparison.Ordinal)) return null;
        return dialog.Replace(tag, tagged, StringComparison.Ordinal);
    }

    // ── Scheduling ────────────────────────────────────────────────────

    private void ScheduleAction(SurgState state)
    {
        _actionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _actionTimer?.Dispose();
        bool urgent = state.HeartStopped || state.BleedVeryQuickly || state.ComingTo;
        int delay   = urgent ? Random.Shared.Next(80, 181) : Random.Shared.Next(150, 451);
        _actionTimer = new System.Threading.Timer(_ => ExecuteAction(state), null, delay, Timeout.Infinite);
    }

    private void ExecuteAction(SurgState state)
    {
        if (!_running) return;

        if (state.PreSurgery)
        {
            // GT format: field|value|\n per field (the ||\n in the log is |\n flattened to ||)
            SendText($"action|dialog_return\ndialog_name|surge\ntilex|{state.TileX}|\ntiley|{state.TileY}|\n");
            Fire("Surgery confirmed — starting");
            _log.Info("[Surg] Pre-surgery confirmed.");
            return;
        }

        int tool = Decide(state);
        if (tool == 0) { Fire("No clear next move"); ToolSlotChanged?.Invoke(-1); return; }

        string nm = Names.GetValueOrDefault(tool, $"tool{tool}");
        SendText($"action|dialog_return\ndialog_name|surgery\nbuttonClicked|tool{tool}|\n");
        Fire($"Used: {nm}");
        _log.Info($"[Surg] → {nm}");
        ToolSlotChanged?.Invoke(-1); // clear hint after sending
    }

    private void RecommendTool(SurgState state)
    {
        if (state.PreSurgery) { ToolRecommended?.Invoke("Confirm surgery"); ToolSlotChanged?.Invoke(-1); return; }
        int tool = Decide(state);
        int slot = tool > 0 && ToolSlot.TryGetValue(tool, out int s) ? s : -1;
        string rec = tool == 0 ? "No clear move" : Names.GetValueOrDefault(tool, $"tool{tool}");
        ToolRecommended?.Invoke(rec);
        ToolSlotChanged?.Invoke(slot);
        Fire($"Next: {rec}");
    }

    // ── Decision engine (correct GT priority) ─────────────────────────

    private static int Decide(SurgState s)
    {
        // 1. DEFIB — heart stopped (2 moves or die)
        if (s.HeartStopped && s.Has(Defib))      return Defib;

        // 2. SPONGE — ONLY when "can't see" (do NOT use proactively!)
        if (s.CantSee && s.Has(Sponge))           return Sponge;

        // 3. ANESTHETIC — patient waking up with open incisions
        if (s.ComingTo && s.Has(Anesthetic))       return Anesthetic;

        // 4. ANESTHETIC — patient awake with open incisions (flail risk)
        if (s.Awake && s.Incisions > 0 && s.Has(Anesthetic)) return Anesthetic;

        // 5. STITCHES — losing blood quickly/very quickly (stop bleed BEFORE fever)
        if (s.BleedingFast && s.Incisions > 0 && s.Has(Stitches)) return Stitches;
        if (s.BleedingFast && s.Has(Transfusion))                  return Transfusion;

        // 6. TRANSFUSION — pulse critical
        if (s.PulseExtremelyWeak && s.Has(Transfusion)) return Transfusion;

        // 7. LAB KIT — ALWAYS use before Ultrasound to check flu/bacterial issues.
        //    Guide: "Use Lab Kit first and Ultrasound before anything else."
        //    Not just when fever — Lab Kit also unlocks Antibiotics for later.
        if (!s.Diagnosed && !s.AntibioticsUnlocked && s.Has(LabKit)) return LabKit;

        // 8. LAB KIT (second pass) — fever develops AFTER diagnosis, still need Lab Kit
        if (s.FeverClimbing && !s.AntibioticsUnlocked && s.Has(LabKit)) return LabKit;

        // ANTIBIOTICS — fever (active climbing or elevated temp)
        if (s.FeverClimbing && s.Has(Antibiotics)) return Antibiotics;
        if (s.Temp > 100f   && s.Has(Antibiotics)) return Antibiotics;

        // 9. ULTRASOUND — diagnose (Splint/Pins only appear after this)
        if (!s.Diagnosed && s.Has(Ultrasound))     return Ultrasound;

        // 10. FLU check — if diagnosed and Fix It never appears, just antibiotics
        if (s.IsFlu && s.Has(Antibiotics))         return Antibiotics;

        // 11. SPLINT — fractures (only available after Ultrasound)
        if (s.HasFractures && s.Has(Splint) && s.Incisions == 0) return Splint;

        // 12. FIX IT — ONLY when patient is UNCONSCIOUS
        if (s.FixItAvailable && s.Unconscious && !s.CantSee && s.Has(FixIt)) return FixIt;

        // 13. SEDATE — must be unconscious before scalpel
        if (!s.Unconscious && s.Has(Scalpel) && s.Has(Anesthetic)) return Anesthetic;

        // 14. SCALPEL — game auto-locks when enough incisions made
        if (s.Unconscious && s.Has(Scalpel))       return Scalpel;

        // 15. PINS — shattered bones (need incision to reach)
        if (s.HasShattered && s.Has(Pins) && s.Incisions > 0) return Pins;

        // 16. SPLINT during open incisions (e.g. Broken Leg with 1 incision)
        if (s.HasFractures && s.Has(Splint))       return Splint;

        // 17. STITCHES — close remaining incisions
        if (s.Incisions > 0 && s.Has(Stitches))   return Stitches;

        // 18. TRANSFUSION — pulse weak (late)
        if (s.PulseWeak && s.Has(Transfusion))     return Transfusion;

        // 19. CLAMP — slow bleeding with open incisions
        if (s.Has(Clamp) && s.Incisions > 0)       return Clamp;

        // 20. Remaining antibiotics (temp elevated but no fever message yet)
        if (s.Temp > 99.5f && s.Has(Antibiotics)) return Antibiotics;

        return 0;
    }

    // ── Parser ────────────────────────────────────────────────────────

    private static SurgState ParseDialog(string d)
    {
        bool pre = d.Contains("SurgeryPrePopup", StringComparison.Ordinal);
        int tx   = ParseEmbed(d, "tilex");
        int ty   = ParseEmbed(d, "tiley");

        // Available tools
        var tools = new HashSet<int>();
        foreach (Match m in Regex.Matches(d, @"add_button_with_icon\|tool(\d+)\|"))
            if (int.TryParse(m.Groups[1].Value, out int id) && id != 4320) tools.Add(id);

        string c = StripColors(d);

        bool cantSee     = d.Contains("can't see what you are doing",     StringComparison.OrdinalIgnoreCase);
        bool heartStop   = d.Contains("Heart stopped",                     StringComparison.OrdinalIgnoreCase);
        bool awake       = d.Contains("Status: `4Awake");
        bool comingTo    = d.Contains("Status: `6Coming to") || d.Contains("Coming to`");
        bool unconscious = d.Contains("Status: `2Unconscious") || d.Contains("Near coma");
        bool pulseWeak   = d.Contains("Pulse: `6Weak");
        bool pulseExWeak = d.Contains("Pulse: `4Extremely");
        bool bleedVQ     = d.Contains("losing blood `4very quickly") || d.Contains("losing blood very quickly");
        bool bleedQ      = !bleedVQ && (d.Contains("losing blood!") || d.Contains("losing blood quickly") || d.Contains("losing blood `4quickly"));
        bool feverClimb  = d.Contains("fever is climbing") || d.Contains("fever is `3climbing")
                        || d.Contains("climbing rapidly") || (d.Contains("fever is slowly rising") && d.Contains("Temp: `4"));
        bool diagnosed   = !d.Contains("has not been diagnosed");
        bool fixIt       = tools.Contains(FixIt);
        bool antibiotics = tools.Contains(Antibiotics);
        bool hasFrac     = c.Contains("broken bone", StringComparison.OrdinalIgnoreCase)
                        || c.Contains("fracture",    StringComparison.OrdinalIgnoreCase)
                        || (tools.Contains(Splint) && diagnosed);
        bool hasShatt    = c.Contains("shattered",   StringComparison.OrdinalIgnoreCase)
                        || tools.Contains(Pins);

        // Flu: if the only treatment shown is Antibiotics and no Fix It ever appeared
        bool isFlu = antibiotics && !fixIt && !tools.Contains(Scalpel) && diagnosed;

        float temp = 98.6f;
        var   tm   = Regex.Match(c, @"Temp:\s*([\d.]+)");
        if (tm.Success) float.TryParse(tm.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out temp);

        int incisions = 0;
        var im = Regex.Match(d, @"Incisions:\s*`\d(\d+)``");
        if (im.Success) int.TryParse(im.Groups[1].Value, out incisions);

        return new SurgState(pre, tx, ty, awake, comingTo, unconscious,
                             heartStop, pulseWeak, pulseExWeak,
                             cantSee, temp, feverClimb,
                             incisions, bleedQ, bleedVQ, hasFrac, hasShatt,
                             diagnosed, isFlu, fixIt, antibiotics, tools);
    }

    private static int ParseEmbed(string d, string key)
    {
        var m = Regex.Match(d, $@"embed_data\|{key}\|(\d+)\|");
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
    }

    private static string StripColors(string s) => Regex.Replace(s, @"`[0-9a-zA-Z$!]", "");
    private static string Flatten(string s) => s.Replace('\n', '|').Replace('\r', ' ').Trim();

    private void SendText(string text)
    {
        var body   = Encoding.Latin1.GetBytes(text + "\0");
        var packet = new byte[4 + body.Length];
        // Dialog returns = GenericText (type 2), not GameMessage (type 3)
        BinaryPrimitives.WriteInt32LittleEndian(packet, NetMessage.GenericText);
        Buffer.BlockCopy(body, 0, packet, 4, body.Length);
        _sendToServer(packet);
    }

    private void LogDialog(string d)
    {
        _log.Info("[Surg] >>> OnDialogRequest:");
        for (int i = 0; i < d.Length; i += 800)
            _log.Info($"[Surg]   {d.Substring(i, Math.Min(800, d.Length - i)).Replace('\n', '|')}");
    }

    private void Fire(string s) => StatusChanged?.Invoke(s);
}
