// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.

namespace KcdMp.Client;

/// <summary>
/// Milestone 3: pure rules for authored/bracketed horse entity names, <c>QuestAskOwnHorse</c> wire encoding
/// (<c>&lt;horse&gt; [tackCsv]</c> or <c>"-"</c>), and equipped tack class extraction from <see cref="WhsSave.HenryParts"/>.
/// </summary>
public static class M3HorseRules
{
    public const int MaxHorseEntityNameLen = 120;
    public const int MaxTackClasses = 16;

    /// <summary>
    /// Validates authored KCD2 horse entity names, including Trosky bracketed/slashed names such as
    /// <c>horse[animalcare/horseCare6_ff15a088-7332-4a17-9536-3e8f6a8898ad]</c>, while rejecting spaces, quotes,
    /// semicolons, control characters, and mod-spawned <c>kcd2mp_*</c> proxies.
    /// </summary>
    public static bool IsHorseEntityName(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > MaxHorseEntityNameLen) return false;
        char c0 = s[0];
        if (!(char.IsAsciiLetterOrDigit(c0) || c0 == '_')) return false;
        if (s.StartsWith("kcd2mp_", StringComparison.OrdinalIgnoreCase)) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok = char.IsAsciiLetterOrDigit(c) || c is '_' or '[' or ']' or '/' or '-' or ':';
            if (!ok) return false;
        }
        return true;
    }

    public static bool IsItemClassGuid(string? s, out string normalized)
    {
        if (s is not null && Guid.TryParseExact(s.Trim(), "D", out var g) && g != Guid.Empty)
        {
            normalized = g.ToString("D");
            return true;
        }
        normalized = "";
        return false;
    }

    public static string NormalizeTackCsv(IEnumerable<string>? classes)
    {
        if (classes is null) return "";
        var sorted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in classes)
        {
            if (IsItemClassGuid(raw, out string norm))
            {
                sorted.Add(norm);
                if (sorted.Count >= MaxTackClasses) break;
            }
        }
        return sorted.Count == 0 ? "" : string.Join(',', sorted);
    }

    public static string FormatOwnHorseAsk(string? horseName, IEnumerable<string>? tackClasses = null)
    {
        if (string.IsNullOrWhiteSpace(horseName) || horseName == "-") return "-";
        if (!IsHorseEntityName(horseName))
            throw new ArgumentException($"Invalid horse entity name '{horseName}'", nameof(horseName));
        string csv = NormalizeTackCsv(tackClasses);
        return csv.Length == 0 ? horseName : $"{horseName} {csv}";
    }

    /// <summary>
    /// Parses a <c>QuestAskOwnHorse</c> or <c>w160_ownhorse</c> payload:
    /// <c>"-"</c> (no horse), <c>"&lt;horseName&gt;"</c>, or <c>"&lt;horseName&gt; &lt;cls1,cls2,...&gt;"</c>.
    /// </summary>
    public static bool TryParseOwnHorseAsk(string? text, out string? horseName, out List<string> tackClasses)
    {
        horseName = null;
        tackClasses = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var f = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length == 1 && f[0] == "-") return true;
        if (f.Length is < 1 or > 2) return false;
        if (!IsHorseEntityName(f[0])) return false;

        var parsed = new SortedSet<string>(StringComparer.Ordinal);
        if (f.Length == 2 && f[1] != "-")
        {
            var parts = f[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > MaxTackClasses) return false;
            foreach (var p in parts)
            {
                if (!IsItemClassGuid(p, out string norm)) return false;
                parsed.Add(norm);
            }
        }
        horseName = f[0];
        tackClasses = parsed.ToList();
        return true;
    }

    /// <summary>Parses Lua <c>m3_peerhorse_ctx &lt;horseName&gt; 1|0</c> event lines.</summary>
    public static bool TryParsePeerHorseContextEvent(string? arg, out string horseName, out bool on)
    {
        horseName = "";
        on = false;
        if (string.IsNullOrWhiteSpace(arg)) return false;
        var f = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 2 || !IsHorseEntityName(f[0]) || f[1] is not ("1" or "0")) return false;
        horseName = f[0];
        on = f[1] == "1";
        return true;
    }

    public static List<string> ExtractEquippedTackClasses(WhsSave.HenryParts? parts) =>
        parts is null ? [] : WhsSave.ExtractHorseEquippedTackClasses(parts);
}
