using System;
using System.Reflection;
using Microsoft.Xna.Framework;

namespace StardewMCP.Agent;

/// <summary>
/// Reads and writes game members by name, for the few Stardew 1.6 members the "funny systems" (#54: clothing, trash
/// cans, night events, horse) could not check at build time. A missing member gives null / false instead of a
/// crash. Net fields (NetRef, NetInt...) are unwrapped to their Value. Game thread only.
/// </summary>
internal static class FunnyReflect
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>A field or property by name (any visibility, base classes included); null when there is none.</summary>
    public static object? Get(object? target, string name)
    {
        if (target == null) return null;
        try
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(f.IsStatic ? null : target);
                PropertyInfo? p = null;
                try { p = t.GetProperty(name, Any | BindingFlags.DeclaredOnly); }
                catch (AmbiguousMatchException) { }
                if (p != null && p.GetIndexParameters().Length == 0 && p.GetMethod != null)
                    return p.GetMethod.Invoke(p.GetMethod.IsStatic ? null : target, null);
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>The Value of a Net field (or the member itself when it is not a wrapper).</summary>
    public static object? Value(object? target, string name) => Unwrap(Get(target, name));

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

    /// <summary>Set the Value of a Net field (e.g. Farmer.shirtItem); false when the member or the setter is missing.</summary>
    public static bool SetValue(object? target, string name, object? value)
    {
        try
        {
            var net = Get(target, name);
            var p = net?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (p == null || p.SetMethod == null) return false;
            p.SetValue(net, value);
            return true;
        }
        catch (Exception) { return false; }
    }

    public static int? Int(object? target, string name)
    {
        var v = Value(target, name);
        if (v == null) return null;
        try { return Convert.ToInt32(v); }
        catch (Exception) { return null; }
    }

    /// <summary>Call a public/non-public instance method with no arguments; false when it is missing or throws.</summary>
    public static bool Call(object? target, string name)
    {
        try
        {
            var m = target?.GetType().GetMethod(name, Any, null, Type.EmptyTypes, null);
            if (m == null) return false;
            m.Invoke(target, null);
            return true;
        }
        catch (Exception) { return false; }
    }
}
