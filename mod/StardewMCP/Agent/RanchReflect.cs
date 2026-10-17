using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// Reads and calls game members by name, for the few members of the Stardew 1.6 API that the buildings and animals
/// tools (#10) could not check at build time (building and animal data fields, animal internals). A member that is
/// not there gives null / false instead of a crash, and the tool then says so in plain words. Net fields (NetInt,
/// NetBool, NetString, NetLong...) are unwrapped to their Value. Game thread only.
/// </summary>
internal static class RanchReflect
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const BindingFlags Declared = Any | BindingFlags.DeclaredOnly;

    /// <summary>Read a field or property by name (any visibility, base classes included); false when there is none or it throws.</summary>
    public static bool TryRead(object? target, Type type, string name, out object? value)
    {
        value = null;
        try
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Declared);
                if (f != null && (f.IsStatic || target != null))
                {
                    value = f.GetValue(f.IsStatic ? null : target);
                    return true;
                }
                PropertyInfo? p = null;
                try { p = t.GetProperty(name, Declared); }
                catch (AmbiguousMatchException) { }
                if (p != null && p.GetIndexParameters().Length == 0 && p.GetMethod is { } getter && (getter.IsStatic || target != null))
                {
                    value = getter.Invoke(getter.IsStatic ? null : target, null);
                    return true;
                }
            }
        }
        catch (Exception)
        {
            value = null;
        }
        return false;
    }

    /// <summary>The member's raw value (a Net field stays a Net field), or null when it is missing or null.</summary>
    public static object? Get(object? target, string name) =>
        target != null && TryRead(target, target.GetType(), name, out var v) ? v : null;

    public static object? GetStatic(Type type, string name) =>
        TryRead(null, type, name, out var v) ? v : null;

    /// <summary>A Net field (NetInt, NetBool...) becomes its Value; anything else is returned as it is.</summary>
    public static object? Unwrap(object? v)
    {
        if (v == null) return null;
        var t = v.GetType();
        if (t.IsPrimitive || t.IsEnum || v is string || v is Point || v is Vector2) return v;
        try
        {
            var p = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.GetIndexParameters().Length == 0 && p.GetMethod != null) return p.GetValue(v);
        }
        catch (Exception) { }
        return v;
    }

    public static string? Str(object? target, string name) => Unwrap(Get(target, name)) as string;

    public static int? Int(object? target, string name)
    {
        var v = Unwrap(Get(target, name));
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is short or byte or ushort) return Convert.ToInt32(v);
        return null;
    }

    public static long? Long(object? target, string name)
    {
        var v = Unwrap(Get(target, name));
        if (v is long l) return l;
        if (v is int i) return i;
        return null;
    }

    public static bool? Bool(object? target, string name) => Unwrap(Get(target, name)) as bool?;

    /// <summary>A Point or Vector2 member (or an object with X and Y) as two ints.</summary>
    public static bool TryPoint(object? target, string name, out int x, out int y)
    {
        x = y = 0;
        var v = Unwrap(Get(target, name));
        switch (v)
        {
            case Point p: x = p.X; y = p.Y; return true;
            case Vector2 vec: x = (int)vec.X; y = (int)vec.Y; return true;
            case null: return false;
        }
        var px = Int(v, "X");
        var py = Int(v, "Y");
        if (px == null || py == null) return false;
        x = px.Value;
        y = py.Value;
        return true;
    }

    /// <summary>Write a field or property by name (a Net field gets its Value set); false when there is none or it can't be written.</summary>
    public static bool Set(object? target, string name, object? value)
    {
        if (target == null) return false;
        try
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Declared);
                if (f != null && !f.IsStatic)
                {
                    var current = f.GetValue(target);
                    if (current != null && SetNetValue(current, value)) return true;
                    if (f.IsInitOnly) return false;
                    f.SetValue(target, value);
                    return true;
                }
                PropertyInfo? p = null;
                try { p = t.GetProperty(name, Declared); }
                catch (AmbiguousMatchException) { }
                if (p != null && p.GetIndexParameters().Length == 0 && p.SetMethod is { IsStatic: false } setter)
                {
                    setter.Invoke(target, new[] { value });
                    return true;
                }
            }
        }
        catch (Exception) { }
        return false;
    }

    private static bool SetNetValue(object net, object? value)
    {
        var t = net.GetType();
        if (t.IsPrimitive || net is string) return false;
        var p = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (p == null || p.SetMethod == null) return false;
        p.SetValue(net, value);
        return true;
    }

    /// <summary>
    /// Call a method by name (any visibility, static or instance; missing optional arguments are filled with their defaults;
    /// whole numbers are widened where a method wants a long or float). <c>found</c> is false when no overload takes these
    /// arguments; <c>error</c> is set when the game method itself threw.
    /// </summary>
    public static (bool found, object? value, string? error) TryCall(object? target, Type type, string name, params object?[] args)
    {
        foreach (var m in type.GetMethods(Any))
        {
            if (!m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || m.IsGenericMethodDefinition) continue;
            if (!m.IsStatic && target == null) continue;
            var ps = m.GetParameters();
            if (args.Length > ps.Length) continue;

            var full = new object?[ps.Length];
            bool fits = true;
            for (int i = 0; i < ps.Length && fits; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt.IsByRef) { fits = false; break; }
                if (i < args.Length)
                {
                    var arg = args[i];
                    if (arg == null) fits = !pt.IsValueType || Nullable.GetUnderlyingType(pt) != null;
                    else if (pt.IsInstanceOfType(arg)) { }
                    else if (arg is int n && pt == typeof(long)) arg = (long)n;
                    else if (arg is int n2 && pt == typeof(float)) arg = (float)n2;
                    else fits = false;
                    full[i] = arg;
                }
                else if (ps[i].HasDefaultValue)
                    full[i] = ps[i].DefaultValue;
                else
                    fits = false;
            }
            if (!fits) continue;

            try
            {
                return (true, m.Invoke(m.IsStatic ? null : target, full), null);
            }
            catch (TargetInvocationException ex)
            {
                return (true, null, $"{type.Name}.{m.Name} failed: {(ex.InnerException ?? ex).Message}");
            }
            catch (Exception ex)
            {
                return (true, null, $"{type.Name}.{m.Name} failed: {ex.Message}");
            }
        }
        return (false, null, null);
    }

    public static (bool found, object? value, string? error) TryCall(object target, string name, params object?[] args) =>
        TryCall(target, target.GetType(), name, args);

    /// <summary>A static method of a game type that is looked up by its full name (so this file needs no reference to it).</summary>
    public static (bool found, object? value, string? error) TryCallStatic(string typeName, string method, params object?[] args)
    {
        var type = typeof(Game1).Assembly.GetType(typeName);
        return type == null ? (false, null, null) : TryCall(null, type, method, args);
    }

    /// <summary>The entries of a game data dictionary (key to value), whatever its generic arguments are.</summary>
    public static List<(string key, object? value)> Entries(object? dict)
    {
        var list = new List<(string, object?)>();
        if (dict is IDictionary d)
        {
            foreach (DictionaryEntry e in d)
                list.Add((e.Key?.ToString() ?? "", e.Value));
        }
        else if (dict is IEnumerable en)
        {
            foreach (var kv in en)
            {
                var key = Get(kv, "Key")?.ToString();
                if (key != null) list.Add((key, Get(kv, "Value")));
            }
        }
        return list;
    }

    /// <summary>Game text with tokens ("[LocalizedText Strings\Buildings:Coop_Name]") as the player sees it; the input when the game can't parse it.</summary>
    public static string DisplayText(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var r = TryCallStatic("StardewValley.TokenizableStrings.TokenParser", "ParseText", raw);
        if (r.found && r.error == null && r.value is string parsed && parsed.Length > 0) return parsed;
        return raw.StartsWith("[") ? fallback : raw;
    }

    /// <summary>The game's own check of a condition string (GameStateQuery); null when it can't be asked.</summary>
    public static bool? CheckConditions(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var r = TryCallStatic("StardewValley.GameStateQuery", "CheckConditions", query, Game1.currentLocation, Game1.player);
        return r.found && r.error == null ? r.value as bool? : null;
    }
}
