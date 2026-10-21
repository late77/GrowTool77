using System.Runtime.InteropServices;

namespace GrowTool77.Aura;

// P/Invoke bindings for Native/aura_enet.c. Only flat functions with simple
// arguments cross the boundary, so no ENet struct layouts are needed here.
internal static class AuraNative
{
    private const string Lib = "aura_enet";

    public const int EventNone       = 0;
    public const int EventConnect    = 1;
    public const int EventDisconnect = 2;
    public const int EventReceive    = 3;

    public const uint PacketReliable = 1;
    public const int  PeerConnected  = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public int    Type;
        public byte   Channel;
        private byte  _pad0, _pad1, _pad2;
        public uint   Data;
        private uint  _pad3;
        public IntPtr Peer;
        public IntPtr Packet;
        public IntPtr Bytes;
        public uint   Length;
        private uint  _pad4;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int  aura_init();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void aura_deinit();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int  aura_event_size();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern IntPtr aura_host_create(int mode, string? bindIp, ushort port, uint peerCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void aura_host_set_flags(IntPtr host, int usingNewPacket, int usingNewPacketForServer);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void aura_host_destroy(IntPtr host);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern IntPtr aura_host_connect(IntPtr host, string ip, ushort port);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int  aura_host_service(IntPtr host, uint timeoutMs, out Event ev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void aura_host_flush(IntPtr host);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void aura_packet_destroy(IntPtr packet);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int  aura_peer_send(IntPtr peer, byte channel, byte[] data, uint length, uint flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int  aura_peer_state(IntPtr peer);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void aura_peer_disconnect(IntPtr peer, uint data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void aura_peer_disconnect_later(IntPtr peer, uint data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void aura_peer_disconnect_now(IntPtr peer, uint data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void aura_peer_timeout(IntPtr peer, uint limit, uint minimum, uint maximum);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern ushort aura_peer_address(IntPtr peer, byte[] buffer, uint length);

    private static int _initialized;

    // Loads the native library once and checks that both sides agree on the event layout.
    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;
        try
        {
            if (aura_event_size() != Marshal.SizeOf<Event>())
                throw new InvalidOperationException("[AURA] native library version mismatch (aura_enet).");
            if (aura_init() != 0)
                throw new InvalidOperationException("[AURA] could not initialise networking.");
        }
        catch
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    public static byte[] CopyPacket(in Event ev)
    {
        var data = new byte[ev.Length];
        if (ev.Length > 0) Marshal.Copy(ev.Bytes, data, 0, (int)ev.Length);
        return data;
    }

    public static string PeerAddress(IntPtr peer)
    {
        var buf  = new byte[64];
        var port = aura_peer_address(peer, buf, (uint)buf.Length);
        if (port == 0) return "?";
        int len = Array.IndexOf(buf, (byte)0);
        return $"{System.Text.Encoding.ASCII.GetString(buf, 0, len < 0 ? buf.Length : len)}:{port}";
    }
}
