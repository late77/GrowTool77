// lang: C#, file: VendServerClient.cs, target: .NET 8 Windows, proj: GrowTool77
// Posts vend data to the local GrowTool77 Vend Server (Ruby/Sinatra).
// Silent failure — GrowTool77 works normally if server is offline.
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GrowTool77.Aura;

namespace GrowTool77.Modules;

public static class VendServerClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private const string BaseUrl = "http://localhost:4567";

    // Called after WorldTracker parses vend data for a world.
    public static async Task PostVendsAsync(string worldName, IReadOnlyList<VendInfo> vends)
    {
        if (vends.Count == 0) return;

        var payload = new
        {
            vends = vends.Select(v => new
            {
                x        = v.X,
                y        = v.Y,
                item_id  = v.ItemId,
                price_wl = v.Price,
            })
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await Http.PostAsync($"{BaseUrl}/worlds/{Uri.EscapeDataString(worldName)}/vends", content)
                      .ConfigureAwait(false);
        }
        catch { /* server offline — silently ignore */ }
    }

    // Fetch historical vend data for a world.
    // VendInfo = record(int X, int Y, int ItemId, int Price)
    public static async Task<List<GrowTool77.Aura.VendInfo>?> GetVendsAsync(string worldName)
    {
        try
        {
            var resp = await Http.GetStringAsync(
                $"{BaseUrl}/worlds/{Uri.EscapeDataString(worldName)}/vends").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(resp);
            var vends = new List<GrowTool77.Aura.VendInfo>();
            foreach (var v in doc.RootElement.GetProperty("vends").EnumerateArray())
            {
                vends.Add(new GrowTool77.Aura.VendInfo(
                    v.GetProperty("tile_x").GetInt32(),
                    v.GetProperty("tile_y").GetInt32(),
                    v.GetProperty("item_id").GetInt32(),
                    v.GetProperty("price_wl").GetInt32()));
            }
            return vends;
        }
        catch { return null; }
    }

    // Search cheapest vend for item across all stored worlds.
    public static async Task<string?> SearchCheapestAsync(int itemId)
    {
        try
        {
            return await Http.GetStringAsync(
                $"{BaseUrl}/vends/search?item_id={itemId}").ConfigureAwait(false);
        }
        catch { return null; }
    }

    // Load ALL vend data from server in one call — used on startup
    // Returns dict[worldName → list of VendInfo]
    public static async Task<Dictionary<string, List<VendInfo>>?> LoadAllAsync()
    {
        try
        {
            var resp = await Http.GetStringAsync($"{BaseUrl}/vends/all").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(resp);
            var result = new Dictionary<string, List<VendInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var world in doc.RootElement.GetProperty("worlds").EnumerateObject())
            {
                var vends = new List<VendInfo>();
                foreach (var v in world.Value.EnumerateArray())
                {
                    vends.Add(new VendInfo(
                        v.GetProperty("x").GetInt32(),
                        v.GetProperty("y").GetInt32(),
                        v.GetProperty("item_id").GetInt32(),
                        v.GetProperty("price_wl").GetInt32()));
                }
                result[world.Name] = vends;
            }
            return result;
        }
        catch { return null; }
    }

    public static async Task<bool> IsOnlineAsync()
    {
        try
        {
            var r = await Http.GetAsync($"{BaseUrl}/ping").ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
