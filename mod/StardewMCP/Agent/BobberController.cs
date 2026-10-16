using System;

namespace StardewMCP.Agent;

/// <summary>
/// Game-free math for the fishing minigame (the game's BobberBar). Keeping it free of game types lets
/// it be unit tested and mirrored in runner/tests/test_bobber_controller.py, which also simulates it.
/// Positions are the bar's own pixels: the track runs 0..568, a bar of height h starts at 0..568-h and
/// the fish sits at 0..532 (measured the way the game compares them, see <see cref="FishInBar"/>).
/// </summary>
public static class BobberMath
{
    public const float TrackLength = 568f;
    public const float FishMax = 532f;

    /// <summary>The fish counts as inside the bar for bar-16 &lt;= fish &lt;= bar+h-44, so the middle of that window is bar = fish - h/2 + 30.</summary>
    public const float CenterOffset = 30f;

    /// <summary>The bar gains or loses this much speed per tick, so a held button and a released button are symmetric.</summary>
    public const float BaseAccel = 0.25f;

    /// <summary>While the fish is inside the bar the acceleration is scaled down; a Trap Bobber scales it further.</summary>
    public const float InBarFactor = 0.6f;
    public const float TrapBobberFactor = 0.3f;

    /// <summary>Aim at where the fish will be this many ticks from now (its speed times this). Tuned in the simulation.</summary>
    public const float Lookahead = 6f;

    /// <summary>Within this many pixels of the aim point keep the previous press/release, which cuts button flicker to about a tenth of the ticks.</summary>
    public const float Deadband = 2f;

    /// <summary>Go for the treasure once the catch bar is above the first value; give up below the second.</summary>
    public const float TreasureStartProgress = 0.55f;
    public const float TreasureStopProgress = 0.35f;

    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>Lowest the bar's start can be (it rests on the bottom of the track).</summary>
    public static float MaxBarPos(float barHeight) => TrackLength - barHeight;

    /// <summary>The game's own in-bar test from BobberBar.update.</summary>
    public static bool FishInBar(float fishPos, float barPos, float barHeight) =>
        fishPos + 12f <= barPos - 32f + barHeight && fishPos - 16f >= barPos - 32f;

    /// <summary>Bar start that puts <paramref name="fishPos"/> in the middle of the in-bar window.</summary>
    public static float CenteredBarPos(float fishPos, float barHeight) =>
        Clamp(fishPos - barHeight / 2f + CenterOffset, 0f, MaxBarPos(barHeight));

    public static float Accel(bool fishInBar, bool trapBobber) =>
        BaseAccel * (fishInBar ? (trapBobber ? TrapBobberFactor : InBarFactor) : 1f);

    /// <summary>Where the fish is expected to be <see cref="Lookahead"/> ticks from now.</summary>
    public static float PredictFish(float fishPos, float fishSpeed) =>
        Clamp(fishPos + fishSpeed * Lookahead, 0f, FishMax);

    /// <summary>Where the bar would come to rest if the opposite acceleration were applied from now on.</summary>
    public static float RestPosition(float barPos, float barSpeed, float accel) =>
        barPos + barSpeed * Math.Abs(barSpeed) / (2f * accel);

    /// <summary>
    /// Bang-bang control with a stop-distance prediction: hold the button (the bar accelerates up) when the
    /// bar would come to rest below the aim point, release it (the bar accelerates down) when it would rest above.
    /// </summary>
    public static bool ShouldPress(float barPos, float barSpeed, float barHeight, float aimFishPos,
        bool fishInBar, bool trapBobber, bool wasPressing)
    {
        float goal = CenteredBarPos(aimFishPos, barHeight);
        float error = RestPosition(barPos, barSpeed, Accel(fishInBar, trapBobber)) - goal;
        if (Math.Abs(error) < Deadband)
            return wasPressing;
        return error > 0f;
    }

    /// <summary>
    /// Whether to chase the treasure chest instead of the fish: only after it has appeared and while the catch
    /// bar is comfortably full, with some hysteresis so the choice does not flip every tick.
    /// </summary>
    public static bool ChaseTreasure(bool hasTreasure, float appearTimer, bool caught, float progress, bool wasChasing)
    {
        if (!hasTreasure || caught || appearTimer > 0f)
            return false;
        return progress > (wasChasing ? TreasureStopProgress : TreasureStartProgress);
    }
}

/// <summary>Game-free math for choosing where to stand and how hard to cast (also mirrored in the Python test).</summary>
public static class CastMath
{
    /// <summary>Tile steps for the directions the game uses: 0 up, 1 right, 2 down, 3 left.</summary>
    public static readonly int[] Dx = { 0, 1, 0, -1 };
    public static readonly int[] Dy = { -1, 0, 1, 0 };

    /// <summary>Aim this far into a tile so a small error in the real landing spot still stays inside it.</summary>
    public const float LandingOffsetTiles = 0.25f;

    /// <summary>The cast distance (in tiles) at full power: (added + 4) sideways, (added + 3) up and down; at least 2 tiles.</summary>
    public static int ReachTiles(int dir, int addedDistance) => addedDistance + (dir == 1 || dir == 3 ? 4 : 3);

    /// <summary>Stand-in for FishingRod.getAddedDistance, used when that method can't be called: extra tiles per Fishing level.</summary>
    public static int AddedDistanceForLevel(int fishingLevel) =>
        fishingLevel >= 15 ? 4 : fishingLevel >= 8 ? 3 : fishingLevel >= 4 ? 2 : fishingLevel >= 1 ? 1 : 0;

    /// <summary>Consecutive fishable tiles from the tile next to (x, y) in direction <paramref name="dir"/>, at most <paramref name="maxScan"/>.</summary>
    public static int RunLength(int x, int y, int dir, int maxScan, Func<int, int, bool> fishable)
    {
        int run = 0;
        for (int i = 1; i <= maxScan; i++)
        {
            if (!fishable(x + Dx[dir] * i, y + Dy[dir] * i))
                break;
            run++;
        }
        return run;
    }

    /// <summary>
    /// How many tiles out to land the bobber, or 0 when this line of water is unusable. Prefers the far side
    /// of the water but keeps one tile of margin before the opposite bank when the run is short, because
    /// an overshoot lands on the bank, wastes the cast and still costs energy.
    /// </summary>
    public static int AimTiles(int run, int reachTiles)
    {
        if (run < 2 || reachTiles < 2)
            return 0;
        int far = Math.Min(reachTiles, run >= 3 ? run - 1 : run);
        return Math.Max(2, far);
    }

    /// <summary>Cast power (0..1) that lands <paramref name="tiles"/> out when full power reaches <paramref name="reachTiles"/>.</summary>
    public static float PowerFor(int tiles, float reachTiles) =>
        BobberMath.Clamp((tiles + LandingOffsetTiles) / reachTiles, 0.05f, 1f);
}
