using System.Runtime.InteropServices;

namespace GrowTool77.Aura;

// P/Invoke bindings for Native/aura_tls.c — the OpenSSL-based HTTPS front door.
// Growtopia's client is OpenSSL and rejects Windows' TLS stack, so the game
// side of the login step must be served by OpenSSL too (same handshake
// options as the working GTProxy).
internal static class AuraTlsNative
{
    private const string Lib = "aura_enet";

    // int cb(user, method, path, body, bodyLen, outBody, outLen, outContentType, contentTypeCap)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int HttpCallback(
        IntPtr user,
        [MarshalAs(UnmanagedType.LPStr)] string method,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        IntPtr body, uint bodyLen,
        out IntPtr outBody, out uint outLen,
        IntPtr outContentType, uint contentTypeCap);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int    aura_tls_global_init();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr aura_tls_create(
        [MarshalAs(UnmanagedType.LPStr)] string certPem, [MarshalAs(UnmanagedType.LPStr)] string keyPem, HttpCallback cb, IntPtr user);
    // Key and self-signed certificate are generated inside OpenSSL, in memory.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr aura_tls_create_selfsigned(HttpCallback cb, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallback(IntPtr user, int ev, [MarshalAs(UnmanagedType.LPStr)] string detail);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void   aura_tls_set_event_cb(IntPtr server, EventCallback cb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int    aura_tls_listen(IntPtr server, ushort port);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void   aura_tls_serve(IntPtr server);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void   aura_tls_stop(IntPtr server);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void   aura_tls_destroy(IntPtr server);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr aura_tls_alloc(uint len);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr aura_tls_get_cert_pem(IntPtr server);

    private static int _init;

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _init, 1) == 1) return;
        if (aura_tls_global_init() != 0)
        {
            Interlocked.Exchange(ref _init, 0);
            throw new InvalidOperationException("[AURA] could not initialise OpenSSL.");
        }
    }
}
