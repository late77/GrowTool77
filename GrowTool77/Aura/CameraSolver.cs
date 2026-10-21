namespace GrowTool77.Aura;

// Where Growtopia's camera is, worked out from a picture of the game window
// and the world's tile map. Nothing is read from the game's memory.
//   1. Tiles are drawn on a strict grid, so edge strength repeats every tile:
//      its period is the on-screen tile size, its offset is the grid phase.
//   2. Each grid cell's average colour gives the visible block boundaries;
//      matching them against the world data finds which tile is in which cell.
// Result: world pixels at the window's left/top edge (CamX/CamY) and tile size.
public sealed record CameraFix(float TilePixels, float CamX, float CamY, float Confidence, bool Matched);

public sealed class ScreenFrame
{
    public byte[] Pixels = Array.Empty<byte>();   // 4 bytes per pixel, any channel order
    public int Width, Height, Stride;
}

public static class CameraSolver
{
    private const float MinTile = 12, MaxTile = 220;

    // The part of the window that shows the world (skips the HUD column and the bottom bar).
    public static (int X, int Y, int W, int H) Area(int width, int height)
    {
        int left = (int)(width * 0.08);
        return (left, 0, width - left, (int)(height * 0.86));
    }

    // Tile-size candidates with grid phase (pixels from the area's left/top edge
    // to the first tile edge), best first. Edge energy also repeats at 2x the
    // tile size, and textured blocks add structure at half of it, so several
    // candidates are returned; matching against the world picks the right one.
    public static List<(float Tile, float PhaseX, float PhaseY, float Score)> GridCandidates(ScreenFrame f)
    {
        var result = new List<(float, float, float, float)>();
        var (ax, ay, aw, ah) = Area(f.Width, f.Height);
        if (aw < 64 || ah < 64) return result;

        var col = new float[aw - 1];
        var row = new float[ah - 1];
        for (int y = 0; y < ah - 1; y++)
        {
            int p  = (ay + y) * f.Stride + ax * 4;
            int pd = p + f.Stride;
            for (int x = 0; x < aw - 1; x++, p += 4, pd += 4)
            {
                float g  = Gray(f.Pixels, p);
                col[x] += MathF.Min(MathF.Abs(Gray(f.Pixels, p + 4) - g), 80);
                row[y] += MathF.Min(MathF.Abs(Gray(f.Pixels, pd) - g), 80);
            }
        }

        int maxLag = (int)MathF.Min(MaxTile * 2, MathF.Min(col.Length, row.Length) / 3f);
        if (maxLag <= MinTile) return result;
        // Light smoothing makes the comb tolerant to sub-pixel drift between teeth.
        col = Smooth(Smooth(col));
        row = Smooth(Smooth(row));
        var cx = Centered(col);
        var cy = Centered(row);
        float colMean = MathF.Max(1e-3f, col.Average()), rowMean = MathF.Max(1e-3f, row.Average());
        var score = new float[maxLag + 2];
        for (int lag = (int)MinTile; lag <= maxLag; lag++)
            score[lag] = AutoCorrelation(cx, lag) + AutoCorrelation(cy, lag);

        // Local maxima of the correlation, strongest first.
        var peaks = Enumerable.Range((int)MinTile + 1, Math.Max(0, maxLag - (int)MinTile - 1))
            .Where(l => score[l] >= score[l - 1] && score[l] >= score[l + 1] && score[l] > 0.15f)
            .OrderByDescending(l => score[l]).Take(6).ToList();
        if (peaks.Count == 0) return result;

        // Fractional refinement of every peak and its divisors (a peak can sit at 2x or 3x the tile).
        var refined = new List<(float Tile, float PhaseX, float PhaseY, float Comb)>();
        foreach (int peak in peaks)
        for (int k = 1; k <= 4; k++)
        {
            float centre = peak / (float)k;
            if (centre < MinTile || centre > MaxTile) continue;
            float span = MathF.Max(1.5f, centre * 0.03f);
            float bestT = centre, bestComb = -1, bpx = 0, bpy = 0;
            for (float t = centre - span; t <= centre + span; t += 0.02f)
            {
                var (sx, px) = Comb(col, t);
                var (sy, py) = Comb(row, t);
                if (sx + sy > bestComb) { bestComb = sx + sy; bestT = t; bpx = px; bpy = py; }
            }
            if (refined.Any(r => MathF.Abs(r.Tile - bestT) <= 0.5f)) continue;
            // Contrast: teeth on the edges vs the same comb shifted half a period.
            // The true tile size lands the shifted comb mid-tile (high contrast);
            // twice the tile size lands it on edges again (≈ 0); half of it, halfway (½).
            float contrast = (CombAt(col, bestT, bpx - 1) - CombAt(col, bestT, bpx - 1 + bestT / 2)) / colMean
                           + (CombAt(row, bestT, bpy - 1) - CombAt(row, bestT, bpy - 1 + bestT / 2)) / rowMean;
            // A multiple of the real tile (2T, 3T, 4T) also has edges at a half,
            // third or quarter of its period; the real tile has none inside a tile.
            float onEdge = CombAt(col, bestT, bpx - 1) + CombAt(row, bestT, bpy - 1);
            bool multiple = false;
            for (int div = 2; div <= 4 && !multiple; div++)
            for (int part = 1; part < div && !multiple; part++)
            {
                float inside = CombAt(col, bestT, bpx - 1 + bestT * part / div) + CombAt(row, bestT, bpy - 1 + bestT * part / div);
                if (inside >= 0.75f * onEdge) multiple = true;
            }
            refined.Add((bestT, bpx, bpy, multiple ? contrast * 0.25f : contrast));
        }
        if (refined.Count == 0) return result;

        // A candidate that is a whole multiple (x2, x3, x4) of a smaller candidate
        // with real contrast of its own is that smaller tile seen every few tiles.
        // (Half a tile also combs, but with about half the contrast — so a smaller
        //  candidate only explains a larger one when its contrast is comparable.)
        var original = refined.Select(r => r.Comb).ToArray();
        for (int a = 0; a < refined.Count; a++)
        for (int b = 0; b < refined.Count; b++)
        {
            if (refined[b].Tile >= refined[a].Tile - 0.5f || original[b] < 0.6f * original[a]) continue;
            float ratio = refined[a].Tile / refined[b].Tile;
            float whole = MathF.Round(ratio);
            if (whole >= 2 && whole <= 4 && MathF.Abs(ratio - whole) < 0.02f * whole)
            {
                refined[a] = refined[a] with { Comb = original[a] * 0.25f };
                break;
            }
        }

        float top = refined.Max(r => r.Comb);
        if (top <= 0) return result;
        var likely = refined.Where(r => r.Comb >= 0.85f * top).OrderBy(r => r.Tile).First();
        result.Add((likely.Tile, likely.PhaseX, likely.PhaseY, likely.Comb / top));
        foreach (var r in refined.OrderByDescending(r => r.Comb))
            if (MathF.Abs(r.Tile - likely.Tile) > 0.5f && r.Comb > 0) result.Add((r.Tile, r.PhaseX, r.PhaseY, r.Comb / top));
        return result;
    }

    // The most likely tile size and grid phase.
    public static (float Tile, float PhaseX, float PhaseY, float Score)? Grid(ScreenFrame f)
    {
        var c = GridCandidates(f);
        return c.Count == 0 ? null : c[0];
    }

    // Full solve: tile size + camera. prior* is where a centred camera would be.
    public static CameraFix? Solve(ScreenFrame f, ushort[] fg, ushort[] bg, int worldW, int worldH,
                                   float playerCentreX, float playerCentreY)
    {
        var candidates = GridCandidates(f);
        if (candidates.Count == 0) return null;

        CameraFix? best = null;
        float bestScore = float.MinValue;
        foreach (var (tile, phx, phy, _) in candidates.Take(4))
        {
            var (priorX, priorY) = CentredPrior(f, worldW, worldH, playerCentreX, playerCentreY, tile);
            var (fix, score) = SolveAt(f, fg, bg, worldW, worldH, priorX, priorY, tile, phx, phy, 14);
            if (fix.Matched && fix.Confidence >= 0.15f) return fix;          // likeliest size first
            if (fix.Matched && score > bestScore) { bestScore = score; best = fix; }
        }
        // No world match (e.g. only sky on screen): fall back to a centred camera at the likeliest tile size.
        var c0 = candidates[0];
        var (px0, py0) = CentredPrior(f, worldW, worldH, playerCentreX, playerCentreY, c0.Tile);
        return best ?? SolveAt(f, fg, bg, worldW, worldH, px0, py0, c0.Tile, c0.PhaseX, c0.PhaseY, 14).Fix;
    }

    // Fast follow-up once the tile size is known: only the grid phase and a
    // world match within a couple of tiles of where the camera should be now.
    // Returns null when the tile size no longer fits (zoom changed) — then a
    // full Solve is needed.
    public static CameraFix? Track(ScreenFrame f, ushort[] fg, ushort[] bg, int worldW, int worldH,
                                   float tile, float predictedCamX, float predictedCamY)
    {
        var (ax, ay, aw, ah) = Area(f.Width, f.Height);
        if (aw < 64 || ah < 64 || tile < MinTile) return null;

        var col = new float[aw - 1];
        var row = new float[ah - 1];
        for (int y = 0; y < ah - 1; y += 2)                // every other row is plenty for the phase
        {
            int p  = (ay + y) * f.Stride + ax * 4;
            int pd = p + f.Stride;
            for (int x = 0; x < aw - 1; x++, p += 4, pd += 4)
            {
                float g  = Gray(f.Pixels, p);
                col[x] += MathF.Min(MathF.Abs(Gray(f.Pixels, p + 4) - g), 80);
                row[y] += MathF.Min(MathF.Abs(Gray(f.Pixels, pd) - g), 80);
            }
        }
        for (int y = 1; y < ah - 1; y += 2) row[y] = row[y - 1];
        col = Smooth(Smooth(col));
        row = Smooth(Smooth(row));

        // Still the same zoom? Teeth on edges must clearly beat teeth mid-tile.
        var (_, phx) = Comb(col, tile);
        var (_, phy) = Comb(row, tile);
        float onEdge  = CombAt(col, tile, phx - 1) + CombAt(row, tile, phy - 1);
        float midTile = CombAt(col, tile, phx - 1 + tile / 2) + CombAt(row, tile, phy - 1 + tile / 2);
        if (onEdge < 1.03f * midTile) return null;          // clearly not this tile size any more (the world match decides the rest)

        var (fix, _) = SolveAt(f, fg, bg, worldW, worldH, predictedCamX, predictedCamY, tile, phx, phy, 2);
        return fix;
    }

    private static (float X, float Y) CentredPrior(ScreenFrame f, int worldW, int worldH, float cx, float cy, float tile)
    {
        float wpp = 32f / tile;
        float viewW = f.Width * wpp, viewH = f.Height * wpp;
        return (Clamp(cx - viewW / 2, worldW * 32f - viewW), Clamp(cy - viewH / 2, worldH * 32f - viewH));
    }

    private static (CameraFix Fix, float Score) SolveAt(ScreenFrame f, ushort[] fg, ushort[] bg, int worldW, int worldH,
                                                        float priorCamX, float priorCamY,
                                                        float tile, float phx, float phy, int radius)
    {
        var (ax, ay, aw, ah) = Area(f.Width, f.Height);
        float worldPerPixel = 32f / tile;
        // Even without recognising the world, the tile edges on screen pin the
        // camera to whole tiles: snap the estimate to the visible grid.
        var fallback = new CameraFix(tile,
            SnapToGrid(priorCamX, (ax + phx) * worldPerPixel),
            SnapToGrid(priorCamY, (ay + phy) * worldPerPixel), 0, false);
        if (fg.Length < worldW * worldH || worldW <= 0 || worldH <= 0) return (fallback, float.MinValue);

        int cols = (int)((aw - phx) / tile);
        int rows = (int)((ah - phy) / tile);
        if (cols < 4 || rows < 3) return (fallback, float.MinValue);

        // Mean colour of the inner part of every cell.
        var mean = new float[cols, rows, 3];
        int step = Math.Max(1, (int)(tile / 10));
        for (int j = 0; j < rows; j++)
        for (int i = 0; i < cols; i++)
        {
            int x0 = ax + (int)(phx + i * tile + tile * 0.25f), x1 = ax + (int)(phx + i * tile + tile * 0.75f);
            int y0 = ay + (int)(phy + j * tile + tile * 0.25f), y1 = ay + (int)(phy + j * tile + tile * 0.75f);
            float r = 0, g = 0, b = 0; int n = 0;
            for (int y = y0; y < y1; y += step)
            for (int x = x0; x < x1; x += step)
            {
                int p = y * f.Stride + x * 4;
                r += f.Pixels[p]; g += f.Pixels[p + 1]; b += f.Pixels[p + 2]; n++;
            }
            if (n == 0) n = 1;
            mean[i, j, 0] = r / n; mean[i, j, 1] = g / n; mean[i, j, 2] = b / n;
        }

        // Visible boundaries between neighbouring cells.
        var edgeH = new bool[cols - 1, rows];
        var edgeV = new bool[cols, rows - 1];
        for (int j = 0; j < rows; j++)
        for (int i = 0; i < cols; i++)
        {
            if (i + 1 < cols) edgeH[i, j] = Distance(mean, i, j, i + 1, j) > 28;
            if (j + 1 < rows) edgeV[i, j] = Distance(mean, i, j, i, j + 1) > 28;
        }

        // World tile at cell (0,0) according to the prior.
        float cell0WorldX = priorCamX + (ax + phx) * worldPerPixel;
        float cell0WorldY = priorCamY + (ay + phy) * worldPerPixel;
        int ox0 = (int)MathF.Round(cell0WorldX / 32f), oy0 = (int)MathF.Round(cell0WorldY / 32f);

        int Radius = radius;
        float bestScore = float.MinValue, secondScore = float.MinValue;
        int bestOx = ox0, bestOy = oy0;
        var scores = new Dictionary<(int, int), float>();
        int totalPairs = (cols - 1) * rows + cols * (rows - 1);
        for (int oy = oy0 - Radius; oy <= oy0 + Radius; oy++)
        for (int ox = ox0 - Radius; ox <= ox0 + Radius; ox++)
        {
            float s = 0; int n = 0;
            for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
            {
                int a = Visual(fg, bg, worldW, worldH, ox + i, oy + j);
                if (a < 0) continue;
                if (i + 1 < cols)
                {
                    int b = Visual(fg, bg, worldW, worldH, ox + i + 1, oy + j);
                    if (b >= 0) { s += Agree(a != b, edgeH[i, j]); n++; }
                }
                if (j + 1 < rows)
                {
                    int b = Visual(fg, bg, worldW, worldH, ox + i, oy + j + 1);
                    if (b >= 0) { s += Agree(a != b, edgeV[i, j]); n++; }
                }
            }
            if (n < totalPairs / 2) continue;
            float norm = s / n;
            scores[(ox, oy)] = norm;
            if (norm > bestScore) { bestScore = norm; bestOx = ox; bestOy = oy; }
        }
        if (bestScore == float.MinValue) return (fallback, float.MinValue);

        foreach (var ((ox, oy), sc) in scores)
            if ((Math.Abs(ox - bestOx) > 1 || Math.Abs(oy - bestOy) > 1) && sc > secondScore) secondScore = sc;

        float confidence = secondScore == float.MinValue ? 1f : (bestScore - secondScore) / MathF.Max(0.05f, MathF.Abs(bestScore));
        if (bestScore <= 0.05f || confidence < 0.08f) return (fallback with { Confidence = confidence }, float.MinValue);

        float camX = bestOx * 32f - (ax + phx) * worldPerPixel;
        float camY = bestOy * 32f - (ay + phy) * worldPerPixel;
        return (new CameraFix(tile, camX, camY, confidence, true), bestScore);
    }

    // What a tile looks like: its block, else its background, else sky (0); outside the world: -1.
    private static int Visual(ushort[] fg, ushort[] bg, int w, int h, int x, int y)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return -1;
        int i = y * w + x;
        return fg[i] != 0 ? fg[i] : bg[i] != 0 ? 65536 + bg[i] : 0;
    }

    private static float Agree(bool predicted, bool observed) =>
        predicted ? (observed ? 2f : -1f) : (observed ? -0.5f : 0.3f);

    private static float Distance(float[,,] m, int i1, int j1, int i2, int j2)
    {
        float dr = m[i1, j1, 0] - m[i2, j2, 0], dg = m[i1, j1, 1] - m[i2, j2, 1], db = m[i1, j1, 2] - m[i2, j2, 2];
        return MathF.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static float Gray(byte[] px, int p) => (px[p] + px[p + 1] + px[p + 2]) / 3f;

    private static float[] Centered(float[] v)
    {
        float mean = v.Average();
        return v.Select(x => x - mean).ToArray();
    }

    private static float AutoCorrelation(float[] v, int lag)
    {
        double dot = 0, a2 = 0, b2 = 0;
        for (int i = 0; i + lag < v.Length; i++)
        {
            dot += v[i] * v[i + lag];
            a2  += v[i] * v[i];
            b2  += v[i + lag] * v[i + lag];
        }
        return (float)(dot / (Math.Sqrt(a2 * b2) + 1e-9));
    }

    // Average edge strength on the grid x = phase + k * period (best phase).
    private static (float Score, float Phase) Comb(float[] profile, float period)
    {
        float best = -1, bestPhase = 0;
        for (float phase = 0; phase < period; phase += 0.25f)
        {
            float sum = 0; int n = 0;
            for (float x = phase; x < profile.Length - 1; x += period)
            {
                int i = (int)x; float t = x - i;
                sum += profile[i] * (1 - t) + profile[i + 1] * t;
                n++;
            }
            if (n > 0 && sum / n > best) { best = sum / n; bestPhase = phase; }
        }
        return (best, bestPhase + 1f);     // a peak at x is the change from pixel x to x+1: the tile starts at x+1
    }

    private static float[] Smooth(float[] v)
    {
        var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
            r[i] = (v[Math.Max(0, i - 1)] + v[i] * 2 + v[Math.Min(v.Length - 1, i + 1)]) / 4f;
        return r;
    }

    private static float CombAt(float[] profile, float period, float phase)
    {
        while (phase < 0) phase += period;
        float sum = 0; int n = 0;
        for (float x = phase; x < profile.Length - 1; x += period)
        {
            int i = (int)x; float t = x - i;
            sum += profile[i] * (1 - t) + profile[i + 1] * t;
            n++;
        }
        return n > 0 ? sum / n : 0;
    }

    // camera + offset must land on a tile edge (a multiple of 32 world pixels).
    private static float SnapToGrid(float cam, float offset) => MathF.Round((cam + offset) / 32f) * 32f - offset;

    private static float Clamp(float wanted, float max) => max < 0 ? max / 2 : Math.Clamp(wanted, 0, max);
}
