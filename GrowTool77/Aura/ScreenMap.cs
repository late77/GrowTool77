namespace GrowTool77.Aura;

// World pixels → screen pixels for the line overlay, modelling Growtopia's
// camera: centred on the player, stopped at the world's edges, and a world
// smaller than the window is centred. tilePixels = on-screen size of a tile.
public readonly struct ScreenMap
{
    public readonly float Scale, CamX, CamY;

    public ScreenMap(float playerX, float playerY, int worldTilesW, int worldTilesH,
                     int screenW, int screenH, float tilePixels)
    {
        Scale = tilePixels / 32f;
        float viewW = screenW / Scale, viewH = screenH / Scale;
        float cx = playerX + 10, cy = playerY + 15;            // centre of the player sprite
        CamX = Camera(cx - viewW / 2, worldTilesW * 32f - viewW);
        CamY = Camera(cy - viewH / 2, worldTilesH * 32f - viewH);
    }

    public (float X, float Y) Player(float playerX, float playerY) => ToScreen(playerX + 10, playerY + 15);

    public (float X, float Y) TileCentre(TilePos t) => ToScreen(t.X * 32f + 16f, t.Y * 32f + 16f);

    public (float X, float Y) ToScreen(float worldX, float worldY) => ((worldX - CamX) * Scale, (worldY - CamY) * Scale);

    private static float Camera(float wanted, float max) => max < 0 ? max / 2 : Math.Clamp(wanted, 0, max);
}
