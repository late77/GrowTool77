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
Section("item name search (real items.dat)");
{
    var path = Path.Combine(research, "grow-items/assets/items.dat");
    if (File.Exists(path))
    {
        var db = ItemDatabase.Parse(File.ReadAllBytes(path));
        var high = db.SearchPrefix("high");
        Check(high.Count > 0 && high.TakeWhile(i => i.Name.StartsWith("high", StringComparison.OrdinalIgnoreCase)).Any(),
              $"\"high\" → {string.Join(", ", high.Take(6).Select(i => i.Name))}…");
        var wl = db.SearchPrefix("world l");
        Check(wl.Any(i => i.Id == 242), $"\"world l\" finds World Lock (#242): {string.Join(", ", wl.Take(4).Select(i => i.Name))}");
        Check(db.SearchPrefix("").Count == 0 && db.SearchPrefix("zzzzqqq").Count == 0, "empty or unknown text gives no suggestions");
    }
    else Console.WriteLine("  skip (items.dat missing)");
}

Section("item icons (.rttex textures)");
{
    var samples = new (string File, int W, int H, string Sha)[]
    {
        ("RTPackJS/test/atomic_button.rttex", 64, 64,   "dd0f900c3f46ce0c1b943f9a9b99767640685264df78a905c093783b21fbfd30"),
        ("RTPackJS/test/news_banner.rttex",   1024, 256, "40fb1b4027218fb3111558cbdb01cf51fd46009d02ba01c1dbbcfa818535de82"),
    };
    foreach (var (file, w, h, sha) in samples)
    {
        var path = Path.Combine(research, file);
        if (!File.Exists(path)) { Console.WriteLine($"  skip {file}"); continue; }
        var img = RtTex.Decode(File.ReadAllBytes(path));
        var hash = img == null ? "" : Convert.ToHexString(SHA256.HashData(img.Rgba)).ToLowerInvariant();
        Check(img != null && img.Width == w && img.Height == h && hash == sha,
              $"{Path.GetFileName(file)} decodes to {img?.Width}x{img?.Height}, pixels identical to the reference decoder");
    }
    Check(RtTex.Decode(new byte[] { 1, 2, 3 }) == null && RtTex.Decode(new byte[200]) == null, "garbage is rejected without crashing");

    var sheet = new RtTexImage { Width = 64, Height = 64, Rgba = Enumerable.Range(0, 64 * 64 * 4).Select(i => (byte)i).ToArray() };
    var cell = RtTex.Cell(sheet, 1, 1);
    Check(cell != null && cell[0] == sheet.Rgba[(32 * 64 + 32) * 4] && RtTex.Cell(sheet, 2, 0) == null, "a 32x32 cell is cut from the right place; outside the sheet → none");

    var dirt = new ItemInfo { TexX = 8, TexY = 20, SpreadType = 2 };
    var rock = new ItemInfo { TexX = 15, TexY = 1, SpreadType = 1 };
    Check(RtTex.IconCell(dirt) == (12, 21) && RtTex.IconCell(rock) == (15, 1), "Dirt (smart edges) uses its stand-alone cell (12,21); Rock uses (15,1)");
}

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
Section("pickups when our drop ids drift from the server's");
{
    // The world says the last drop id is 5, but the server numbers from 100.
    WorldTracker NewWorld()
    {
        var w = Worlds.Build();
        var t = new WorldTracker { Items = ItemDatabase.Parse(File.ReadAllBytes(Path.Combine(research, "grow-items/assets/items.dat"))) };
        t.Enter(WorldParser.Parse(w, 0, w.Length, null));
        return t;
    }
    int Wl(WorldTracker t) => t.Snapshot()!.Items.Where(i => i.ItemId == 242).Sum(i => i.Amount);
    bool Upd(WorldTracker t, byte[] pkt) { GameUpdate.TryRead(pkt, out var gu); return t.Apply(gu); }

    // 1) The user's exact case: drop 10 → pick up → drop 10 again.
    var a = NewWorld();
    Check(Wl(a) == 2, "start: 2 World Locks on the floor");
    Upd(a, Pkt.Game(14, netId: -1, floatVar: 10f, intData: 242, x: 320, y: 320));
    Check(Wl(a) == 12, "drop 10 WL → 12");
    var r1 = a.NotePickupRequest(101, 320, 320);                     // the game asks for drop #101
    Check(r1.Contains("renumbered"), $"the game's pickup request fixes our numbering ({r1})");
    Check(Upd(a, Pkt.Game(14, netId: 7, itemNetId: 101)) && Wl(a) == 2, "server removal for #101 now matches → 2");
    Upd(a, Pkt.Game(14, netId: -1, floatVar: 10f, intData: 242, x: 320, y: 320));
    Check(Wl(a) == 12, "drop 10 WL again → 12, not 22");
    Check(a.NotePickupRequest(102, 320, 320) == "id matches" && Upd(a, Pkt.Game(14, netId: 7, itemNetId: 102)) && Wl(a) == 2,
          "next pickup: ids already in step, removal matches directly");

    // 2) Server removal never seen: "Collected 10 World Lock" applies the pickup.
    var b = NewWorld();
    Upd(b, Pkt.Game(14, netId: -1, floatVar: 10f, intData: 242, x: 640, y: 320));
    b.NotePickupRequest(200, 640, 320);
    var r2 = b.NoteCollected(10, 242);
    Check(r2 == "removed" && Wl(b) == 2, $"\"Collected\" message removes it when the server removal is missing ({r2})");
    Check(!Upd(b, Pkt.Game(14, netId: 7, itemNetId: 200, x: 640, y: 320)) && Wl(b) == 2,
          "a late removal for the same drop doesn't remove anything else");

    // 3) Partial pickup (inventory full): server sets the new amount, "Collected 6" must not subtract again.
    var c = NewWorld();
    Upd(c, Pkt.Game(14, netId: -1, floatVar: 10f, intData: 242, x: 960, y: 320));
    c.NotePickupRequest(300, 960, 320);
    Upd(c, Pkt.Game(14, netId: -3, itemNetId: 300, floatVar: 4f, intData: 242, x: 960, y: 320));
    var r3 = c.NoteCollected(6, 242);
    Check(Wl(c) == 2 + 4 && r3 == "already applied", $"partial pickup: 4 left on the floor, not subtracted twice ({r3})");

    // 4) Another player's pickup with a drift: matched by the drop's position.
    var d = NewWorld();
    Upd(d, Pkt.Game(14, netId: -1, floatVar: 10f, intData: 242, x: 1280, y: 320));
    Check(Upd(d, Pkt.Game(14, netId: 55, itemNetId: 400, x: 1281, y: 319)) && Wl(d) == 2, "someone else's pickup is matched by position");
    Check(!Upd(d, Pkt.Game(14, netId: 55, itemNetId: 999, x: 3000, y: 1800)) && Wl(d) == 2, "a pickup far from any drop is ignored");

    // 5) Colour codes in the console text are handled.
    Check(AuraProxy.StripColors("Collected `w10 World Lock``. Rarity: `w0``") == "Collected 10 World Lock. Rarity: 0",
          "Growtopia colour codes are stripped from the console message");
}

Section("placed blocks and drop positions");
{
    var world = Worlds.Build();
    var t = new WorldTracker();
    t.Enter(WorldParser.Parse(world, 0, world.Length, null));
    var view = t.Snapshot()!;
    Check(view.Width == 100 && view.Height == 60 && view.PlacedComplete, $"world size {view.Width}x{view.Height}, every tile read");
    var lockTile = view.Placed.FirstOrDefault(p => p.ItemId == 242 && !p.Background);
    Check(lockTile != null && lockTile.Count == 1 && lockTile.Tiles[0] == new TilePos(1, 0), "World Lock placed at tile (1,0)");
    var bg = view.Placed.FirstOrDefault(p => p.ItemId == 14 && p.Background);
    Check(bg != null && bg.Count == 6000, $"background layer counted separately ({bg?.Count} tiles)");
    var gems = view.Items.First(i => i.ItemId == 112);
    Check(gems.Tiles.Count == 1 && gems.Tiles[0] == new TilePos(2, 3), $"gems lie on tile {gems.Tiles[0]}");
}

Section("line overlay camera model");
{
    // 100x60 world, 1280x720 window, 32 px tiles (1:1), player in the middle.
    var m = new ScreenMap(1600 - 10, 960 - 15, 100, 60, 1280, 720, 32);
    var (px, py) = m.Player(1600 - 10, 960 - 15);
    Check(Math.Abs(px - 640) < 0.01 && Math.Abs(py - 360) < 0.01, $"player in the middle of the world is drawn at the window centre ({px},{py})");
    var (tx, ty) = m.TileCentre(new TilePos(52, 30));
    Check(Math.Abs(tx - (640 + 2 * 32 - 0)) < 0.01 + 16 && Math.Abs(ty - 360) <= 32,
          $"a tile two to the right is drawn ~64 px to the right ({tx},{ty})");

    // Player at the top-left corner: camera stops at the world edge.
    var c = new ScreenMap(0, 0, 100, 60, 1280, 720, 32);
    var (cx, cy) = c.Player(0, 0);
    Check(Math.Abs(cx - 10) < 0.01 && Math.Abs(cy - 15) < 0.01, $"at the world's corner the camera stops: player drawn at ({cx},{cy})");

    // Zoomed out to 16 px tiles: the same tile offset is half as far on screen.
    var z = new ScreenMap(1600 - 10, 960 - 15, 100, 60, 1280, 720, 16);
    var (zx, _) = z.TileCentre(new TilePos(60, 30));
    var (zx0, _) = z.Player(1600 - 10, 960 - 15);
    Check(Math.Abs((zx - zx0) - 10 * 16 - 8) < 1, $"line scale shrinks distances (10 tiles → {zx - zx0:0} px at 16 px/tile)");

    // A world smaller than the window is centred.
    var s = new ScreenMap(0, 0, 10, 10, 1280, 720, 32);
    var (sx, _) = s.TileCentre(new TilePos(0, 0));
    Check(Math.Abs(sx - ((1280 - 320) / 2f + 16)) < 0.01, $"a small world is centred ({sx})");
}

Section("camera solver (lines start at the player at any zoom)");
{
    // Real screenshot from Growtopia: the tile size must come out ~52-53 px.
    var real = Path.Combine(research, "real_screen_1901x1138.rgba");
    if (File.Exists(real))
    {
        var frame = new ScreenFrame { Width = 1901, Height = 1138, Stride = 1901 * 4, Pixels = File.ReadAllBytes(real) };
        var grid = CameraSolver.Grid(frame);
        Check(grid != null && grid.Value.Tile > 51.5f && grid.Value.Tile < 53.5f,
              $"real Growtopia screenshot: tile size {grid?.Tile:0.00} px (zoomed in; 32 would be 100%)");
    }

    var (fg, bg) = FakeScreen.World(100, 60, 11);
    var cases = new (float Tile, float CamX, float CamY)[]
    {
        (52.5f, 1000.3f, 300.7f), (32f, 0f, 0f), (24f, 900f, 500f), (64f, 1700.2f, 980.9f), (41.7f, 1500f, 600f), (36f, 1700f, 1000f), (47.3f, 512.6f, 1100.2f),
    };
    foreach (var (tile, camX, camY) in cases)
    {
        const int W = 1600, H = 1000;
        float wpp = 32f / tile;
        // Growtopia doesn't keep the player exactly centred: put it 1.7 tiles above centre.
        float playerX = camX + W * wpp / 2 - 10, playerY = camY + H * wpp / 2 - 1.7f * 32 - 15;
        var frame = FakeScreen.Render(fg, bg, 100, 60, W, H, tile, camX, camY, playerX, playerY);
        var fix = CameraSolver.Solve(frame, fg, bg, 100, 60, playerX + 10, playerY + 15);
        bool ok = fix != null && fix.Matched && MathF.Abs(fix.TilePixels - tile) < 0.2f
                  && MathF.Abs(fix.CamX - camX) < 2.5f && MathF.Abs(fix.CamY - camY) < 2.5f;
        Check(ok, $"tile {tile} px, camera ({camX},{camY}) → solved {fix?.TilePixels:0.00} px, ({fix?.CamX:0.0},{fix?.CamY:0.0}), confidence {fix?.Confidence:0.00}");
    }
}

Section("lines on a 150 % display (the user's case): from the eyes to the targets");
{
    // Growtopia stretched by Windows to 150 %: a tile is 48 screen pixels.
    var (fg, bg) = FakeScreen.World(100, 60, 77);
    const int W = 1400, H = 860;
    const float tile = 48f; float wpp = 32f / tile;
    float camX = 1400.4f, camY = 900.6f;
    // The game keeps the player ~1.5 tiles above the centre.
    float px = camX + W * wpp / 2 - 10, py = camY + H * wpp / 2 - 48 - 15;
    var frame = FakeScreen.Render(fg, bg, 100, 60, W, H, tile, camX, camY, px, py);
    var targets = new List<TilePos> { new(60, 40), new(61, 40), new(62, 41), new(50, 35) };

    var fix = CameraSolver.Solve(frame, fg, bg, 100, 60, px + 10, py + 15);
    var geo = LineGeometry.Compute(fix, px + 10, py + 15, px, py, 100, 60, W, H, 0, 0, targets);
    (float, float) Truth(float wx, float wy) => ((wx - camX) / wpp, (wy - camY) / wpp);
    float Err((float X, float Y) a, (float X, float Y) b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    var eyesTrue = Truth(px + LineGeometry.EyeX, py + LineGeometry.EyeY);
    Check(fix != null && fix.Matched && Err(geo.Eyes, eyesTrue) < 3, $"lines start at the eyes: drawn {geo.Eyes}, true {eyesTrue}");
    float worst = targets.Select((t, i) => Err(geo.Ends[i], Truth(t.X * 32 + 16, t.Y * 32 + 16))).Max();
    Check(worst < 3, $"every line ends on its target tile (worst error {worst:0.0} px at {tile} px tiles)");

    // The player walks 3 tiles right; the game's camera follows. Before the next check:
    float dx = 96;
    var moved = LineGeometry.Compute(fix, px + 10, py + 15, px + dx, py, 100, 60, W, H, 0, 0, targets);
    (float, float) TruthMoved(float wx, float wy) => ((wx - camX - dx) / wpp, (wy - camY) / wpp);
    Check(Err(moved.Eyes, TruthMoved(px + dx + LineGeometry.EyeX, py + LineGeometry.EyeY)) < 3
          && Err(moved.Ends[0], TruthMoved(60 * 32 + 16, 40 * 32 + 16)) < 3,
          "after walking, the lines still start at the eyes and end on the targets");
}

Section("lines snapshot file");
{
    // A real PNG (the decoded test texture re-encoded by Python is not available here),
    // so build the smallest valid one: 1x1 pixel.
    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    var withData = PngText.Insert(png, "growtool77-lines", "{\"tile\":48,\"matched\":true}");
    Check(PngText.Read(withData, "growtool77-lines") == "{\"tile\":48,\"matched\":true}" && PngText.Read(png, "growtool77-lines") == null,
          "diagnostic data is stored inside the PNG and read back");
    Check(withData.AsSpan(withData.Length - 12, 4).SequenceEqual("\0\0\0\0"u8) && System.Text.Encoding.ASCII.GetString(withData, withData.Length - 8, 4) == "IEND",
          "the PNG still ends with its IEND chunk (opens normally)");
}

Section("camera follow-up while the player moves (no lag)");
{
    var (fg, bg) = FakeScreen.World(100, 60, 4242);
    const int W = 1400, H = 860; float tile = 52f, wpp = 32f / tile;
    float camX = 1300, camY = 700, px = camX + W * wpp / 2 - 10, py = camY + H * wpp / 2 - 48 - 15;
    var fix = CameraSolver.Solve(FakeScreen.Render(fg, bg, 100, 60, W, H, tile, camX, camY, px, py), fg, bg, 100, 60, px + 10, py + 15);
    Check(fix != null && fix.Matched, "camera locked once with the full search");
    int good = 0; double slowest = 0;
    var rnd = new Random(5);
    for (int step = 0; step < 10; step++)
    {
        float dx = (float)(rnd.NextDouble() - 0.5) * 60, dy = (float)(rnd.NextDouble() - 0.5) * 40;
        camX += dx; camY += dy; px += dx; py += dy;
        var frame = FakeScreen.Render(fg, bg, 100, 60, W, H, tile, camX, camY, px, py);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = CameraSolver.Track(frame, fg, bg, 100, 60, fix!.TilePixels, fix.CamX + dx + (step % 2) * 12, fix.CamY + dy);
        slowest = Math.Max(slowest, sw.Elapsed.TotalMilliseconds);
        if (t != null && t.Matched && MathF.Abs(t.CamX - camX) < 3 && MathF.Abs(t.CamY - camY) < 3) { good++; fix = t; }
    }
    Check(good == 10, $"followed the camera 10/10 times within 3 px ({good}/10), slowest {slowest:0.0} ms");
    var zoomed = FakeScreen.Render(fg, bg, 100, 60, W, H, 80f, camX, camY, px, py);
    var z = CameraSolver.Track(zoomed, fg, bg, 100, 60, fix!.TilePixels, fix.CamX, fix.CamY);
    Check(z == null || !z.Matched, "after zooming, the follow-up asks for a new full search");
}

Section("Shifty Block colour and arrows");
{
    var c = new ShiftyColor();
    var a0 = c.At(0); var a1 = c.At(1.5);
    Check(!c.Synced && (a0.R, a0.G, a0.B) != (a1.R, a1.G, a1.B), $"free-running: the colour keeps turning ({a0} → {a1})");

    // The game's block measured on screen at 300 °/s (as the tracker would, ~12 times a second).
    double rate = 300;
    for (int i = 0; i < 12; i++)
    {
        double t = 10 + i * 0.083;
        var (r, g, b) = ShiftyColor.FromHue(40 + rate * (t - 10));
        c.Observe(r, g, b, t);
    }
    var predicted = c.At(11.5);
    var truth = ShiftyColor.FromHue(40 + rate * 1.5);
    int diff = Math.Abs(predicted.R - truth.R) + Math.Abs(predicted.G - truth.G) + Math.Abs(predicted.B - truth.B);
    Check(c.Synced && Math.Abs(c.DegreesPerSecond - rate) < 5 && diff < 30,
          $"locked onto the on-screen Shifty Block: {c.DegreesPerSecond:0} °/s, half a second later {predicted} vs game {truth}");
    Check(!c.Observe(128, 128, 128, 20), "grey (no Shifty Block visible) is ignored");

    var geo = new LineGeometry.Result(48, (500, 400), new List<(float X, float Y)> { (900, 400), (510, 405) });
    var arrows = LineGeometry.Arrows(geo);
    var far = arrows[0]; var near = arrows[1];
    Check(MathF.Abs(far.Angle) < 0.01f && far.Tip.X > 500 + 48 * 1.7f && far.Tip.X < 900 && far.Length == 48,
          $"far target: a one-tile arrow next to the player pointing at it (tip x {far.Tip.X:0})");
    Check(MathF.Abs(near.Angle - MathF.PI / 2) < 0.01f && near.Tip.Y < 405, "target under the player: arrow pointing down at it");
    var outline = LineGeometry.Outline(far);
    Check(outline.Length == 7 && outline[0] == far.Tip, "arrow outline starts at its tip");
}

Section("vend locator");
{
    var w = Worlds.Build();
    var snap = WorldParser.Parse(w, 0, w.Length, null);
    Check(snap.Vends.Count == 1 && snap.Vends[0] == new VendInfo(4, 0, 242, -5),
          $"vending machine read from world data: {string.Join(", ", snap.Vends)}");
    Check(Math.Abs(snap.Vends[0].WlPerItem - 0.2) < 1e-9, "price -5 = 5 items per WL = 0.2 WL each");

    var a = Worlds.Build(extraVends: new[] { (242, 3), (242, -10), (1796, 7), (242, 0) }, name: "SHOPA");
    var b = Worlds.Build(extraVends: new[] { (242, 1), (242, -2) }, name: "SHOPB");
    var t = new WorldTracker();
    t.Enter(WorldParser.Parse(a, 0, a.Length, null));
    t.Leave();
    t.Enter(WorldParser.Parse(b, 0, b.Length, null));
    var vends = t.VendSnapshot();
    Check(vends.Count == 2, "vends of every world entered this session are kept (in memory)");

    var hits = VendSearch.Find(vends, 242);
    var order = string.Join(" < ", hits.Select(h => $"{h.World}:{VendSearch.PriceText(h.Price)}"));
    // each test world also has the base vend at tile 4 (World Lock, 5 for 1 WL)
    Check(hits.Count == 6 && string.Join(",", hits.Select(h => h.Price)) == "-10,-5,-5,-2,1,3",
          $"cheapest first across worlds, unpriced skipped: {order}");
    Check(hits[0].World == "SHOPA" && hits[0].X == 1 && hits[0].Y == 1, $"position of the cheapest vend is known ({hits[0].World} {hits[0].X},{hits[0].Y})");
    Check(VendSearch.Find(vends, 9999).Count == 0, "an item nobody sells gives no results");
}

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

    // Full pickup via a remove packet (net id is a player id).
    {
        var w2 = new WorldTracker();
        w2.Enter(WorldParser.Parse(Worlds.Build(), 0, Worlds.Build().Length, null));
        int before = w2.Snapshot()!.Items.Count;
        // add a fresh drop, then collect it entirely
        GameUpdate.TryRead(Pkt.Game(14, netId: -1, floatVar: 3f, intData: 400, x: 50, y: 50), out var addA);
        w2.Apply(addA);
        uint uid = (uint)(5 + 1); // last uid in Worlds.Build() is 5, first new drop is 6
        GameUpdate.TryRead(Pkt.Game(14, netId: 1234, itemNetId: (int)uid), out var collect);
        bool removed = w2.Apply(collect);
        Check(removed && w2.Snapshot()!.Items.Count == before, "item picked up via remove packet disappears from the scan");
    }

    // Full pickup signalled as a change-to-zero (-3 with amount 0).
    {
        var w3 = new WorldTracker();
        w3.Enter(WorldParser.Parse(Worlds.Build(), 0, Worlds.Build().Length, null));
        // gems drop uid 3 has amount 5; change it to 0 → gone
        var zero = Pkt.Game(14, netId: -3, itemNetId: 3, floatVar: 0f, intData: 112);
        GameUpdate.TryRead(zero, out var gz);
        w3.Apply(gz);
        Check(w3.Snapshot()!.Gems == 0, "a drop changed to amount 0 is removed from the scan");
    }

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
    Check(HostsFile.Domains.Contains("growtopia1.com") && HostsFile.Domains.Contains("growtopia2.com")
          && HostsFile.Domains.Contains("www.growtopia1.com") && HostsFile.Domains.Contains("www.growtopia2.com"),
          "hosts block redirects both the www and non-www login domains");
    Check(HostsFile.AddBlock(original).Split('\n').Count(l => l.Contains("127.0.0.1")) == 5,  // 4 Growtopia names + the original localhost line
          "hosts block writes all four 127.0.0.1 lines");
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
    proxy.DebugDrops = true;
    var itemsPath = Path.Combine(research, "grow-items/assets/items.dat");
    if (File.Exists(itemsPath)) proxy.SetItemDatabase(ItemDatabase.Parse(File.ReadAllBytes(itemsPath)));
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
            Check(Net.WaitUntil(() => log.Lines().Any(l => l.Contains("Drop packet [drop]") && l.Contains("applied")), all),
                  "debug mode logs the drop packet and whether it matched");

            serverB.Send(bConn.Peer, Pkt.Game(14, netId: 7, itemNetId: 3));
            Check(Net.WaitUntil(() => lastWorld?.Gems == 10, all), "live pickup: gems 15 → 10");

            var bigUp = new byte[40_000];
            new Random(7).NextBytes(bigUp);
            BinaryPrimitives.WriteInt32LittleEndian(bigUp, 2);
            game.Send(toProxy2, bigUp);
            var up = Net.Expect(serverB, AuraNative.EventReceive, all, 15000);
            Check(up != null && up.Data.AsSpan().SequenceEqual(bigUp), "40 KB packet game → [AURA] → server arrives intact (integrity header per fragment)");

            // The user's case end-to-end: drop 10 WL, pick it up, drop again.
            int WlNow() => lastWorld?.Items.Where(i => i.ItemId == 242).Sum(i => i.Amount) ?? -1;
            serverB.Send(bConn.Peer, Pkt.Game(14, netId: -1, floatVar: 10f, intData: 242, x: 800, y: 800));
            Check(Net.WaitUntil(() => WlNow() == 12, all), "relay: drop 10 WL → 12");
            game.Send(toProxy2, Pkt.Game(11, netId: 7, intData: 900, x: 800, y: 800));          // the game asks for drop #900
            Net.Expect(serverB, AuraNative.EventReceive, all);                                    // ...which reaches the server first,
            serverB.Send(bConn.Peer, Pkt.Game(14, netId: 7, itemNetId: 900));                     // then the server removes #900
            serverB.Send(bConn.Peer, Pkt.Variant("OnConsoleMessage", "Collected `w10 World Lock``. Rarity: `w0``"));
            Check(Net.WaitUntil(() => WlNow() == 2, all), "relay: picked up → 2");
            serverB.Send(bConn.Peer, Pkt.Game(14, netId: -1, floatVar: 10f, intData: 242, x: 800, y: 800));
            Check(Net.WaitUntil(() => WlNow() == 12, all) && !Net.WaitUntil(() => WlNow() == 22, all, 400), "relay: dropped again → 12, not 22");

            game.Send(toProxy2, Pkt.Game(0, netId: 7, x: 480, y: 704));
            Check(Net.WaitUntil(() => proxy.TryGetPlayer(out var px, out var py) && px == 480 && py == 704, all),
                  "the player's position is read from the game's own movement packets");

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
    var upstreamMap = new Dictionary<string, IPAddress> { ["www.growtopia1.com"] = IPAddress.Loopback };

    // 1) normal login, upstream certificate not trusted by the OS → continue with a warning
    {
        using var server = new FakeLoginServer(18444, _ => (200, new[] { "Content-Type: text/html" }, Encoding.ASCII.GetBytes(reply), false));
        (string, ushort)? target = null;
        var log = new AuraLog();
        using var relay = new LoginRelay(log, upstreamMap, () => 16999, (ip, port) => target = (ip, port), upstreamPort: 18444);
        relay.Start(httpsPort: 18443, httpPort: 0);

        var self = await relay.SelfTestAsync();
        Check(self.Ok, $"self-test of the local secure endpoint: {self.Message}");

        var (status, body) = await GameHttp.SendAsync(18443);
        Check(status == 200 && body.Contains("server|127.0.0.1\n") && body.Contains("port|16999\n") && body.Contains("type2|1"),
              "reply tells the game to connect to [AURA]");
        Check(body.Contains("meta|abc123") && body.Contains("#maint|Maintenance") && body.Contains("RTENDMARKERBS1001"),
              "all other lines are passed through unchanged");
        Check(target == ("213.179.209.168", (ushort)17198), $"[AURA] learns the real server {target}");
        Check(server.Requests.Count == 1 && server.Requests[0].Head == GameHttp.Head() && server.Requests[0].Body == GameHttp.Body,
              "the game's request reaches the real server byte-for-byte (same headers, same order)");
        var lines = string.Join("\n", log.Lines());
        Check(lines.Contains("certificate not validated") && !lines.Contains("version=5.39"),
              "untrusted certificate is reported in the log; request body is never logged");
    }

    // 1b) the OpenSSL front door also serves a client pinned to TLS 1.2 (older game builds)
    {
        using var server = new FakeLoginServer(18484, _ => (200, Array.Empty<string>(), Encoding.ASCII.GetBytes(reply), false));
        using var relay = new LoginRelay(new AuraLog(), upstreamMap, () => 16999, (_, _) => { }, upstreamPort: 18484);
        relay.Start(httpsPort: 18483, httpPort: 0);

        async Task<string?> HandshakeAsync(System.Security.Authentication.SslProtocols protocols)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, 18483);
                using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "www.growtopia1.com",
                    EnabledSslProtocols = protocols,
                });
                return ssl.SslProtocol.ToString();
            }
            catch (Exception) { return null; }
        }

        var tls12 = await HandshakeAsync(System.Security.Authentication.SslProtocols.Tls12);
        Check(tls12 == "Tls12", $"OpenSSL front door serves a TLS 1.2 client: {tls12 ?? "failed"}");
    }

    // 2) strict mode refuses an untrusted server
    {
        using var server = new FakeLoginServer(18454, _ => (200, Array.Empty<string>(), Encoding.ASCII.GetBytes(reply), false));
        (string, ushort)? target = null;
        using var relay = new LoginRelay(new AuraLog(), upstreamMap, () => 16999, (ip, port) => target = (ip, port),
                                         upstreamPort: 18454, strictUpstreamCertificate: true);
        relay.Start(httpsPort: 18453, httpPort: 0);
        var (status, _) = await GameHttp.SendAsync(18453);
        Check(status == 502 && target == null, "strict mode: an untrusted server is refused");
    }

    // 3) start-up check with a chunked + gzip reply, then the game's own request gets 403 → fallback
    {
        using var server = new FakeLoginServer(18464, i => i == 0
            ? (200, new[] { "Content-Type: text/html", "Content-Encoding: gzip" }, GameHttp.Gzip(reply), true)
            : (403, new[] { "Content-Type: text/html" }, Encoding.ASCII.GetBytes("<h1>403 Forbidden</h1>"), false));
        (string, ushort)? target = null;
        var log = new AuraLog();
        using var relay = new LoginRelay(log, upstreamMap, () => 16999, (ip, port) => target = (ip, port), upstreamPort: 18464);
        relay.Start(httpsPort: 18463, httpPort: 0);

        var pre = await relay.PrefetchAsync("5.39", "225");
        Check(pre.Ok, $"start-up check decodes a chunked + gzip reply: {pre.Message}");
        Check(server.Requests.Count == 1 && server.Requests[0].Head.Contains("User-Agent: UbiServices_SDK") && server.Requests[0].Body == GameHttp.Body,
              "start-up check sends the same request shape as the game");

        var (status, body) = await GameHttp.SendAsync(18463);
        Check(status == 200 && body.Contains("server|127.0.0.1\n") && target == ("213.179.209.168", (ushort)17198),
              "game's request answered 403 → [AURA] uses the reply from the start-up check");
        Check(string.Join("\n", log.Lines()).Contains("Using the reply [AURA] fetched"), "fallback is written to the log");
    }

    // 4) 403 with no fallback → passed to the game and shown as a failed step
    {
        using var server = new FakeLoginServer(18474, _ => (403, new[] { "Content-Type: text/html" }, Encoding.ASCII.GetBytes("forbidden"), false));
        string? step = null;
        using var relay = new LoginRelay(new AuraLog(), upstreamMap, () => 16999, (_, _) => { }, upstreamPort: 18474);
        relay.StepChanged += s => step = s;
        relay.Start(httpsPort: 18473, httpPort: 0);
        var (status, _) = await GameHttp.SendAsync(18473);
        Check(status == 403 && step != null && step.StartsWith("Login step 3 failed"), $"403 is passed on and reported: \"{step}\"");
    }
}

Section("upstream server selection (real login server before CDN)");
{
    // Mirrors the user's actual DNS: www.* is an Akamai CDN (193.229.113.x),
    // the bare name is the real AWS login server (52.44.105.194).
    var upstream = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase)
    {
        ["growtopia1.com"]     = IPAddress.Parse("52.44.105.194"),
        ["www.growtopia1.com"] = IPAddress.Parse("193.229.113.81"),
        ["growtopia2.com"]     = IPAddress.Parse("35.168.190.185"),
        ["www.growtopia2.com"] = IPAddress.Parse("193.229.113.75"),
    };
    var order = LoginRelay.UpstreamOrder("www.growtopia1.com", upstream, HostsFile.Domains);
    Check(order[0].ToString() == "52.44.105.194", $"request for www.growtopia1.com tries the real login server first ({order[0]})");
    Check(order.IndexOf(IPAddress.Parse("52.44.105.194")) < order.IndexOf(IPAddress.Parse("193.229.113.81")),
          "the Akamai CDN address is tried only after the real login servers");
    Check(LoginRelay.UpstreamOrder("growtopia1.com", upstream, HostsFile.Domains)[0].ToString() == "52.44.105.194",
          "request for growtopia1.com also starts at the real login server");
}

Section("DNS-over-HTTPS resolver");
{
    var log = new AuraLog();
    var doh = new DohResolver(log);
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ip = await doh.ResolveAsync("one.one.one.one", cts.Token);
        if (ip != null)
            Check(ip.ToString() is "1.1.1.1" or "1.0.0.1", $"DoH resolves one.one.one.one → {ip}");
        else
            Console.WriteLine("  skip DoH live test (no outbound HTTPS in this sandbox)");

        Check((await doh.ResolveAsync("52.44.105.194")) ?.ToString() == "52.44.105.194",
              "DoH returns an IP literal unchanged");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  skip DoH live test ({ex.GetType().Name})");
    }
}

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
