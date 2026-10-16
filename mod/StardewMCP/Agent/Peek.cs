using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace StardewMCP.Agent;

/// <summary>
/// Fail-soft reads of game members by name. The quest, board and special-order classes of Stardew Valley 1.6 keep
/// many members under names that differ between versions (fields vs properties, Netcode wrappers), so the tools in
/// QuestTools / QuestEvents read them through this class: a member that is missing or throws reads as "not there"
/// instead of breaking the build or the tool call. A name ending in "()" calls a method without arguments.
/// Netcode wrappers (NetInt, NetString, NetRef...) are unwrapped to their Value.
/// </summary>
internal static class Peek
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static object? Get(object? target, string name) => Read(target?.GetType(), target, name);

    public static object? GetStatic(Type? type, string name) => Read(type, null, name);

    private static object? Read(Type? type, object? target, string name)
    {
        if (type == null) return null;
        try
        {
            bool call = name.EndsWith("()", StringComparison.Ordinal);
            var member = call ? name[..^2] : name;
            for (var t = type; t != null; t = t.BaseType)
            {
                if (call)
                {
                    var m = t.GetMethods(Declared).FirstOrDefault(x => x.Name == member && x.GetParameters().Length == 0);
                    if (m != null) return Unwrap(m.Invoke(target, null));
                    continue;
                }
                var f = t.GetField(member, Declared);
                if (f != null) return Unwrap(f.GetValue(target));
                var p = t.GetProperty(member, Declared);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return Unwrap(p.GetValue(target));
            }
        }
        catch (Exception)
        {
            // reads as "not there"
        }
        return null;
    }

    /// <summary>NetInt / NetString / NetRef... to the value inside (collections have no Value and stay as they are).</summary>
    private static object? Unwrap(object? value)
    {
        for (int i = 0; i < 3 && value != null; i++)
        {
            var t = value.GetType();
            if (t.Namespace != "Netcode") break;
            var prop = t.GetProperty("Value");
            if (prop == null) break;
            value = prop.GetValue(value);
        }
        return value;
    }

    /// <summary>First non-empty text among the named members, trimmed ("" when none).</summary>
    public static string Text(object? target, params string[] names)
    {
        foreach (var n in names)
            if (Get(target, n) is string s && s.Trim().Length > 0)
                return s.Trim();
        return "";
    }

    /// <summary>First numeric member among the named ones, or null when none can be read.</summary>
    public static int? Int(object? target, params string[] names)
    {
        foreach (var n in names)
        {
            switch (Get(target, n))
            {
                case int i: return i;
                case long l: return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
                case uint u: return u > int.MaxValue ? int.MaxValue : (int)u;
                case short s: return s;
                case byte b: return b;
                case float f: return (int)f;
                case double d: return (int)d;
            }
        }
        return null;
    }

    /// <summary>First bool member among the named ones, or null when none can be read.</summary>
    public static bool? Flag(object? target, params string[] names)
    {
        foreach (var n in names)
            if (Get(target, n) is bool b)
                return b;
        return null;
    }

    /// <summary>The elements of a list-like member (empty when it is missing or not a list).</summary>
    public static List<object> Items(object? collection)
    {
        var list = new List<object>();
        if (collection is IEnumerable seq && collection is not string)
            foreach (var o in seq)
                if (o != null)
                    list.Add(o);
        return list;
    }

    /// <summary>One line: game markup (^ = line break) and runs of spaces collapsed.</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return Regex.Replace(s.Replace('^', ' '), @"\s+", " ").Trim();
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 3)].TrimEnd() + "...";
}
