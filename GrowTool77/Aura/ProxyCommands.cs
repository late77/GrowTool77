// lang: C#, file: ProxyCommands.cs, target: .NET 8 Windows, proj: GrowTool77
// Intercepts /pdest, /pid and /cldd from the game's chat before they reach
// the server. Shows inline feedback in the Growtopia client window.
//
// /pdest <N>  — prepare N random 7-char destinations (:XXXXXXX)
// /pid        — ready to assign own IDs (matching destinations, no colon)
// /cldd       — clear all stored dest/id data
//
// When a door/portal is wrenched while a session is active, the proxy
// pre-fills the Destination or ID field of the Edit dialog before the
// game sees it. The player still presses OK themselves.
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace GrowTool77.Aura;

public sealed class ProxyCommands
{
    // Stored destinations and IDs (always same count, in order).
    private readonly List<string> _destinations = new(); // ":XXXXXXX"
    private readonly List<string> _ids          = new(); // "XXXXXXX"  (no colon)
    private bool _lockDoors   = false;
    private bool _pdestActive = false;   // wrenching fills destinations
    private bool _pidActive   = false;   // wrenching fills IDs
    private int  _nextIdx     = 0;

    private int  _targetCount = 0;

    // Set by AuraProxy — used to send messages to game and detect door dialogs
    public Action<byte[]>? SendToGame     { get; set; }
    public Action<byte[]>? SendToServer   { get; set; }

    // Called by AuraProxy when a door dialog arrives from the server.
    // Returns a modified dialog string if we should pre-fill it, else null.
    public string? OnDoorDialog(string dialog)
    {
        if (!_pdestActive && !_pidActive) return null;
        if (_nextIdx >= _destinations.Count && _nextIdx >= _ids.Count) return null;

        if (_pdestActive && _nextIdx < _destinations.Count)
        {
            string dest = _destinations[_nextIdx++];
            // Pre-fill Destination field and optionally lock the door.
            string mod = FillTextField(dialog, "destination", dest);
            if (_lockDoors) mod = SetLockFlag(mod, true);
            if (_nextIdx >= _destinations.Count) { _pdestActive = false; SendChat("pdest done!"); }
            return mod;
        }

        if (_pidActive && _nextIdx < _ids.Count)
        {
            string id = _ids[_nextIdx++];
            // Pre-fill ID field.
            string mod = FillTextField(dialog, "id", id);
            if (_nextIdx >= _ids.Count) { _pidActive = false; SendChat("pid done!"); }
            return mod;
        }

        return null;
    }

    // Called by AuraProxy from InspectFromGame for every game→server text.
    // Returns true if the packet should be DROPPED (not forwarded to server).
    public bool OnGameText(string text)
    {
        // Extract chat text using various GT protocol formats
        string? chat = ExtractChat(text);
        if (chat == null || !chat.StartsWith("/")) return false;

        if (HandlePdest(chat)) return true;
        if (HandlePid(chat))   return true;
        if (HandleCldd(chat))  return true;

        return false;
    }

    // ── Command handlers ─────────────────────────────────────────────

    private bool HandlePdest(string cmd)
    {
        // /pdest [N] [lock]
        var m = Regex.Match(cmd, @"^/pdest\s+(\d+)(\s+lock)?", RegexOptions.IgnoreCase);
        if (!m.Success) { if (cmd.Equals("/pdest", StringComparison.OrdinalIgnoreCase)) { SendChat("Usage: /pdest <count> [lock]"); return true; } return false; }

        int count = int.Parse(m.Groups[1].Value);
        _lockDoors = m.Groups[2].Success;
        count = Math.Clamp(count, 1, 500);

        _destinations.Clear(); _ids.Clear();
        for (int i = 0; i < count; i++)
        {
            string rand = RandomId(7);
            _destinations.Add(":" + rand);
            _ids.Add(rand);
        }
        _targetCount = count;
        _nextIdx     = 0;
        _pdestActive = true;
        _pidActive   = false;
        SendChat($"pdest ready — wrench {count} doors in order" + (_lockDoors ? " (lock)" : ""));
        return true;
    }

    private bool HandlePid(string cmd)
    {
        if (!cmd.Equals("/pid", StringComparison.OrdinalIgnoreCase)) return false;
        if (_ids.Count == 0) { SendChat("No session — run /pdest first."); return true; }
        _nextIdx   = 0;
        _pidActive = true; _pdestActive = false;
        SendChat("pid ready — wrench the same doors in order to assign IDs");
        return true;
    }

    private bool HandleCldd(string cmd)
    {
        if (!cmd.Equals("/cldd", StringComparison.OrdinalIgnoreCase)) return false;
        _destinations.Clear(); _ids.Clear();
        _nextIdx = 0; _pdestActive = false; _pidActive = false;
        SendChat("done!");
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    // Fill a named text_input field in a Growtopia dialog string.
    private static string FillTextField(string dialog, string fieldName, string value)
    {
        // Format: add_text_input|<name>|<current>|<maxlen>|
        string pattern = $@"(add_text_input\|{Regex.Escape(fieldName)}\|)([^|]*)(\|)";
        return Regex.Replace(dialog, pattern, m => m.Groups[1].Value + value + m.Groups[3].Value);
    }

    private static string SetLockFlag(string dialog, bool locked)
    {
        // Check-box or radio for "locked" — look for add_check_box|locked
        // The actual field name may vary; this handles the common "locked" checkbox.
        string pattern = @"(add_check_box\|locked\|)([01])(\|)";
        return Regex.Replace(dialog, pattern, m => m.Groups[1].Value + (locked ? "1" : "0") + m.Groups[3].Value);
    }

    // Try every known GT chat packet format to extract what the player typed.
    // GT has used different formats across versions — check them all.
    private static string? ExtractChat(string text)
    {
        // Known key names for the chat/text field in GT protocol packets
        string[] keys = { "character|", "ch|", "chat|", "msg|", "text|", "input|" };
        foreach (string key in keys)
        {
            int idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            string after = text[(idx + key.Length)..];
            int end  = after.IndexOfAny(new[] { '\n', '\r' });
            string chat = (end >= 0 ? after[..end] : after).Trim();
            if (chat.Length > 0) return chat;
        }
        return null;
    }

    private static string RandomId(int len)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var sb = new System.Text.StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append(chars[Random.Shared.Next(chars.Length)]);
        return sb.ToString();
    }

    // Send a short message to the game client chat (appears like a server notice).
    private void SendToGameChat(string msg)
    {
        if (SendToGame == null) return;
        // Build action|log|msg|... GenericText packet
        string text = $"action|log|msg|`6[GrowTool77]`` {msg}";
        var body    = Encoding.Latin1.GetBytes(text + "\0");
        var packet  = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, NetMessage.GenericText);
        Buffer.BlockCopy(body, 0, packet, 4, body.Length);
        SendToGame(packet);
    }

    private void SendChat(string msg) => SendToGameChat(msg);
}
