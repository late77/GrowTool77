using System.Security.Cryptography.X509Certificates;

namespace GrowTool77.Aura;

public enum CertResult { Ok, Cancelled, Failed }

// Growtopia (OpenSSL-based) checks [AURA]'s HTTPS certificate and rejects a
// self-signed one with "unknown ca". The fix other proxies use is to make the
// system trust that certificate. [AURA] installs its certificate into the
// current user's "Trusted Root Certification Authorities" store while it runs
// and removes it on stop. The per-user store needs no administrator rights,
// and only certificates [AURA] added (marked by a known friendly name) are
// ever touched.
public static class CertTrust
{
    private const string FriendlyName = "GrowTool77 [AURA] login certificate";

    // Installs the PEM certificate as a trusted root for this user.
    // Returns the thumbprint on success.
    public static (CertResult Result, string? Thumbprint) Install(string certPem)
    {
        try
        {
            using var cert = X509Certificate2.CreateFromPem(certPem);
            using var persist = new X509Certificate2(cert.Export(X509ContentType.Cert))
            {
                FriendlyName = FriendlyName,
            };

            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            RemoveAuraCerts(store);                 // clear any leftovers first
            store.Add(persist);
            store.Close();
            return (CertResult.Ok, persist.Thumbprint);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // On some systems the user declines the one-time "add certificate" prompt.
            return (CertResult.Cancelled, null);
        }
        catch (Exception)
        {
            return (CertResult.Failed, null);
        }
    }

    // Removes every certificate [AURA] added to this user's root store.
    public static CertResult Remove()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            RemoveAuraCerts(store);
            store.Close();
            return CertResult.Ok;
        }
        catch (Exception)
        {
            return CertResult.Failed;
        }
    }

    public static bool HasLeftover()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            bool found = store.Certificates.Cast<X509Certificate2>()
                .Any(c => c.FriendlyName == FriendlyName);
            store.Close();
            return found;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RemoveAuraCerts(X509Store store)
    {
        foreach (var c in store.Certificates.Cast<X509Certificate2>()
                     .Where(c => c.FriendlyName == FriendlyName))
        {
            try { store.Remove(c); } catch (System.Security.Cryptography.CryptographicException) { }
        }
    }
}
