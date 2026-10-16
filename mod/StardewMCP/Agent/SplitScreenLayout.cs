using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// THROWAWAY spike for issue #37 (follow-up): recording-friendly split-screen.
///   - 3 local players get an even 2x2 grid (P1 top-left, P2 top-right, P3 bottom-left, bottom-right empty)
///     instead of the game's "P1 full-width on top" layout.
///   - hides the host "Paused" banner, the gamepad button tutorial, and connection chat lines.
/// </summary>
public static class SplitScreenLayout
{
    private static IMonitor _monitor = null!;

    public static bool Grid3 = true;
    public static bool HidePausedBanner = true;
    public static bool HideJoinChat = true;
    public static bool HideCursor = true;

    /// <summary>Chat info keys (Strings\UI:Chat_*) that are hidden from every screen's chat box.</summary>
    public static readonly HashSet<string> HiddenChatKeys = new() { "BuildingMagicBuild" };

    private static readonly Vector4[] Grid =
    {
        new(0f, 0f, 0.5f, 0.5f),
        new(0.5f, 0f, 0.5f, 0.5f),
        new(0f, 0.5f, 0.5f, 0.5f),
    };

    private static string? _pausedText;
    private static bool _inAddPlayer;

    public static void Apply(IManifest manifest, IMonitor monitor, string which)
    {
        _monitor = monitor;
        monitor.Log($"[spike] Harmony patches: {which}", LogLevel.Info);
        if (which == "none")
            return;
        var h = new Harmony(manifest.UniqueID + ".spike37");
        h.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.SetWindowSize)),
            transpiler: new HarmonyMethod(typeof(SplitScreenLayout), nameof(SetWindowSize_Transpiler)));
        h.Patch(AccessTools.Method(typeof(GameRunner), "Draw"),
            postfix: new HarmonyMethod(typeof(SplitScreenLayout), nameof(GameRunnerDraw_Postfix)));
        if (which == "layout")
            return;
        h.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.drawMouseCursor)),
            prefix: new HarmonyMethod(typeof(SplitScreenLayout), nameof(DrawMouseCursor_Prefix)));
        h.Patch(AccessTools.Method(typeof(IClickableMenu), nameof(IClickableMenu.drawMouse)),
            prefix: new HarmonyMethod(typeof(SplitScreenLayout), nameof(DrawMouseCursor_Prefix)));
        h.Patch(AccessTools.PropertyGetter(typeof(Game1), nameof(Game1.IsHudDrawn)),
            postfix: new HarmonyMethod(typeof(SplitScreenLayout), nameof(IsHudDrawn_Postfix)));
        h.Patch(AccessTools.Method(typeof(SpriteText), nameof(SpriteText.drawStringWithScrollBackground)),
            prefix: new HarmonyMethod(typeof(SplitScreenLayout), nameof(ScrollText_Prefix)));
        h.Patch(AccessTools.Method(typeof(Multiplayer), nameof(Multiplayer.addPlayer)),
            prefix: new HarmonyMethod(typeof(SplitScreenLayout), nameof(AddPlayer_Prefix)),
            finalizer: new HarmonyMethod(typeof(SplitScreenLayout), nameof(AddPlayer_Finalizer)));
        h.Patch(AccessTools.Method(typeof(Multiplayer), "receiveChatInfoMessage"),
            prefix: new HarmonyMethod(typeof(SplitScreenLayout), nameof(ChatInfo_Prefix)));
        h.Patch(AccessTools.Method(typeof(ChatBox), nameof(ChatBox.addInfoMessage)),
            prefix: new HarmonyMethod(typeof(SplitScreenLayout), nameof(AddInfoMessage_Prefix)));
    }

    // ------------------------------------------------------------------ capture (game frame only, never the desktop)

    public static volatile string? PendingCapture;
    public static volatile string? LastCapture;

    /// <summary>After GameRunner.Draw has composed every screen into the back buffer, save it as PNG if asked.</summary>
    private static void GameRunnerDraw_Postfix()
    {
        var path = PendingCapture;
        if (path == null)
            return;
        PendingCapture = null;
        try
        {
            var gd = Game1.graphics.GraphicsDevice;
            int w = gd.PresentationParameters.BackBufferWidth, h = gd.PresentationParameters.BackBufferHeight;
            var data = new Color[w * h];
            gd.GetBackBufferData(data);
            for (int i = 0; i < data.Length; i++)
                data[i].A = 255;
            using var tex = new Texture2D(gd, w, h);
            tex.SetData(data);
            using var fs = File.Create(path);
            tex.SaveAsPng(fs, w, h);
            LastCapture = $"{path} ({w}x{h})";
        }
        catch (Exception ex)
        {
            LastCapture = "error: " + ex.Message;
        }
    }

    /// <summary>Per-screen tick: keep the gamepad button tutorial from ever showing.</summary>
    public static void Tick()
    {
        if (!Context.IsWorldReady)
            return;
        // DecoratableLocation/Farm.resetLocalState add a ButtonTutorialMenu the first time (mail flag not yet set).
        Game1.player.mailReceived.Add("button_tut_1");
        Game1.player.mailReceived.Add("button_tut_2");
        for (int i = Game1.onScreenMenus.Count - 1; i >= 0; i--)
            if (Game1.onScreenMenus[i] is ButtonTutorialMenu)
                Game1.onScreenMenus.RemoveAt(i);
    }

    // ------------------------------------------------------------------ 2x2 layout for 3 players

    /// <summary>
    /// SetWindowSize picks this screen's rectangle with <c>list[game1.instanceIndex]</c> from a fixed table.
    /// Insert a call right after that lookup so 3 players use the 2x2 grid; the rest of the method
    /// (render targets, viewport, menu repositioning) then runs unchanged with the new rectangle.
    /// </summary>
    private static IEnumerable<CodeInstruction> SetWindowSize_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getItem = AccessTools.Method(typeof(List<Vector4>), "get_Item");
        var hook = AccessTools.Method(typeof(SplitScreenLayout), nameof(PickRect));
        int patched = 0;
        foreach (var ins in instructions)
        {
            yield return ins;
            if (ins.Calls(getItem))
            {
                yield return new CodeInstruction(OpCodes.Call, hook);
                patched++;
            }
        }
        if (patched != 1)
            throw new InvalidOperationException($"SetWindowSize: expected 1 List<Vector4>.get_Item, found {patched}");
    }

    private static Vector4 PickRect(Vector4 original)
    {
        if (!Grid3 || GameRunner.instance.gameInstances.Count != 3)
            return original;
        int i = Game1.game1.instanceIndex;
        return i >= 0 && i < Grid.Length ? Grid[i] : original;
    }

    // ------------------------------------------------------------------ recording artefacts

    /// <summary>Skip the "Paused" scroll the game draws on every screen while the host has paused the world.</summary>
    private static bool ScrollText_Prefix(string s)
    {
        if (!HidePausedBanner || !Context.IsWorldReady || !Game1.HostPaused)
            return true;
        _pausedText ??= Game1.content.LoadString("Strings\\StringsFromCSFiles:DayTimeMoneyBox.cs.10378");
        return s != _pausedText;
    }

    /// <summary>The game hides the whole HUD (toolbar, clock, energy) while the host has paused; keep it so frozen frames match live ones.</summary>
    private static void IsHudDrawn_Postfix(ref bool __result)
    {
        if (__result || !HidePausedBanner || !Game1.HostPaused)
            return;
        __result = (Game1.displayHUD || Game1.eventUp) && Game1.gameMode == 3 && !Game1.freezeControls && !Game1.panMode && !Game1.game1.takingMapScreenshot;
    }

    /// <summary>Nobody uses a mouse: farmhand screens show a snapped gamepad cursor in menus, P1 shows the real mouse.</summary>
    private static bool DrawMouseCursor_Prefix() => !HideCursor;

    private static void AddPlayer_Prefix() => _inAddPlayer = true;

    private static Exception? AddPlayer_Finalizer(Exception? __exception)
    {
        _inAddPlayer = false;
        return __exception;
    }

    /// <summary>"X (127.0.0.1) has joined" is added straight to the chat box inside Multiplayer.addPlayer.</summary>
    private static bool AddInfoMessage_Prefix() => !(HideJoinChat && _inAddPlayer);

    private static bool ChatInfo_Prefix(string messageKey) => !(HideJoinChat && HiddenChatKeys.Contains(messageKey));

    // ------------------------------------------------------------------ per-screen zoom

    /// <summary>Set this screen's base zoom / UI scale (split-screen multiplies both by zoomModifier 0.5).</summary>
    public static object SetZoom(float? zoom, float? ui)
    {
        if (zoom is float z)
            Game1.options.desiredBaseZoomLevel = z;
        if (ui is float u)
            Game1.options.desiredUIScale = u;
        return new
        {
            ok = true,
            screen = Context.ScreenId,
            baseZoom = Game1.options.desiredBaseZoomLevel,
            uiScale = Game1.options.desiredUIScale,
            effectiveZoom = Game1.options.baseZoomLevel * Game1.game1.zoomModifier,
        };
    }
}
