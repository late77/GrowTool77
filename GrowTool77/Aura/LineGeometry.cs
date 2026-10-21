namespace GrowTool77.Aura;

// Where to draw the lines, in the overlay's pixels (= screen pixels under it).
// Pure function so it can be tested; LineOverlay only paints the result.
public static class LineGeometry
{
    // Eyes relative to the player's position (world pixels).
    public const float EyeX = 10, EyeY = 9;

    public sealed record Result(float Tile, (float X, float Y) Eyes, IReadOnlyList<(float X, float Y)> Ends);

    // fix + solvedCX/CY: the latest camera check and the player's centre at that moment (null = none yet).
    public static Result Compute(CameraFix? fix, float solvedCX, float solvedCY,
                                 float playerX, float playerY, int worldW, int worldH,
                                 int screenW, int screenH, float biasX, float biasY,
                                 IReadOnlyList<TilePos> targets)
    {
        float pcx = playerX + 10, pcy = playerY + 15;
        float tile = fix?.TilePixels ?? 32f;
        float camX, camY;
        if (fix != null)
        {
            // The camera follows the player: carry the checked camera along until the next check.
            camX = fix.CamX + (pcx - solvedCX);
            camY = fix.CamY + (pcy - solvedCY);
            if (fix.Matched)
            {
                float viewW = screenW * 32f / tile, viewH = screenH * 32f / tile;
                camX = Edge(camX, worldW * 32f - viewW);
                camY = Edge(camY, worldH * 32f - viewH);
            }
        }
        else
        {
            var map = new ScreenMap(playerX + biasX, playerY + biasY, worldW, worldH, screenW, screenH, tile);
            camX = map.CamX;
            camY = map.CamY;
        }

        float s = tile / 32f;
        (float, float) ToScreen(float wx, float wy) => ((wx - camX) * s, (wy - camY) * s);
        var ends = targets.Select(t => ToScreen(t.X * 32f + 16f, t.Y * 32f + 16f)).ToList();
        return new Result(tile, ToScreen(playerX + EyeX, playerY + EyeY), ends);
    }

    // One arrow per target, like GrowScan's: a tile long, near the player,
    // pointing at the target. Close targets get the arrow right next to them;
    // a target under the player gets an arrow pointing down at it.
    public sealed record Arrow((float X, float Y) Tip, float Angle, float Length, float Width);

    public static IReadOnlyList<Arrow> Arrows(Result geo)
    {
        float tile = geo.Tile;
        float length = tile, width = tile * 0.72f, orbit = tile * 1.7f;
        var arrows = new List<Arrow>();
        foreach (var end in geo.Ends)
        {
            float dx = end.X - geo.Eyes.X, dy = end.Y - geo.Eyes.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist < tile * 0.5f)
            {
                arrows.Add(new Arrow((end.X, end.Y - tile * 0.45f), MathF.PI / 2, length, width));   // pointing down
                continue;
            }
            float ux = dx / dist, uy = dy / dist;
            float tipDist = MathF.Min(orbit + length / 2, dist - tile * 0.35f);
            arrows.Add(new Arrow((geo.Eyes.X + ux * tipDist, geo.Eyes.Y + uy * tipDist), MathF.Atan2(uy, ux), length, width));
        }
        return arrows;
    }

    // The arrow's outline in screen pixels: head + shaft, tip first.
    public static (float X, float Y)[] Outline(Arrow a)
    {
        float l = a.Length, w = a.Width, head = l * 0.55f;
        var local = new (float X, float Y)[]
        {
            (0, 0), (-head, -w / 2), (-head, -w / 4), (-l, -w / 4), (-l, w / 4), (-head, w / 4), (-head, w / 2),
        };
        float c = MathF.Cos(a.Angle), s = MathF.Sin(a.Angle);
        return local.Select(p => (a.Tip.X + p.X * c - p.Y * s, a.Tip.Y + p.X * s + p.Y * c)).ToArray();
    }

    // The camera stops at the world's edges; a world smaller than the view is centred.
    private static float Edge(float wanted, float max) => max < 0 ? max / 2 : Math.Clamp(wanted, 0, max);
}
