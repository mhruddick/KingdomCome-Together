// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.

namespace KcdMp.Client.Tests;

public class M1PlayerDropLuaTests
{
    private static string RepoRoot()
    {
        string d = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(d, "dotnet", "KcdMp.Client"))) d = Path.GetDirectoryName(d)!;
        return d;
    }

    [Fact]
    public void Kdcmp_lua_has_no_utf8_bom_and_wires_placing_drop_pickup_guards()
    {
        string luaPath = Path.Combine(RepoRoot(), "kdcmp", "Data", "Scripts", "Startup", "kdcmp.lua");
        byte[] raw = File.ReadAllBytes(luaPath);
        Assert.True(raw.Length > 3);
        Assert.False(raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "kdcmp.lua must NEVER start with a UTF-8 BOM (Lua 5.1 rejects BOMs)");

        string text = System.Text.Encoding.UTF8.GetString(raw);
        Assert.Contains("mp_item_finalize(key, d, \"sync\")", text);
        Assert.Contains("Script.SetTimer(60, function()", text);
        Assert.Contains("mp_item_finalize(key, d, \"fast\")", text);
        Assert.Contains("function KCD2MP_ItemTryBindPlacingByName(name)", text);
        Assert.Contains("d.spawnInvCount ~= nil and curCount > d.spawnInvCount", text);
        Assert.Contains("KCD2MP_ItemTryBindPlacingByName(name)", text);
    }
}
