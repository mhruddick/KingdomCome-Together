// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.

namespace KcdMp.Client.Tests;

public class M1ScriptSdkFallbackTests
{
    private static string RepoRoot()
    {
        string d = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(d, "dotnet", "KcdMp.Client"))) d = Path.GetDirectoryName(d)!;
        return d;
    }

    [Theory]
    [InlineData("Publish-Release.ps1")]
    [InlineData("Build-Installer.ps1")]
    public void Release_and_installer_scripts_only_fall_back_to_local_sdk8_when_dotnet_is_not_on_path(string scriptName)
    {
        string path = Path.Combine(RepoRoot(), "tools", scriptName);
        string content = File.ReadAllText(path);
        Assert.Contains("if (-not $env:DOTNET_ROOT -and -not (Get-Command dotnet -ErrorAction SilentlyContinue))", content);
        Assert.DoesNotContain("if (-not $env:DOTNET_ROOT) {\r\n", content);
        Assert.DoesNotContain("if (-not $env:DOTNET_ROOT) {\n", content);
    }
}
