// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class M3HorseRulesTests
{
    private const string SaddleCls = "bf06d242-4dcb-48e9-bbf1-b6a36f368a57";
    private const string BridleCls = "505343b8-b5ff-4f47-94d9-470a1977a421";

    [Theory]
    [InlineData("tsem_sedivka", true)]
    [InlineData("nebakovPruzkum_herynk", true)]
    [InlineData("tsem_horseForSale_2", true)]
    [InlineData("horse[animalcare/horseCare6_ff15a088-7332-4a17-9536-3e8f6a8898ad]", true)]
    [InlineData("randomEvent[event/horse_trader:01]", true)]
    [InlineData("", false)]
    [InlineData("-", false)]
    [InlineData("[leading_bracket]", false)]
    [InlineData("kcd2mp_horse_1", false)]
    [InlineData("KCD2MP_peerhorse_2", false)]
    [InlineData("horse with space", false)]
    [InlineData("horse\"quote", false)]
    [InlineData("horse;cmd", false)]
    [InlineData("horse\\slash", false)]
    public void IsHorseEntityName_accepts_authored_and_bracketed_names_and_rejects_unsafe_inputs(string input, bool expected)
    {
        Assert.Equal(expected, M3HorseRules.IsHorseEntityName(input));
    }

    [Fact]
    public void IsHorseEntityName_enforces_120_char_cap_matching_Protocol_MaxHorseNameLen()
    {
        Assert.Equal(120, Protocol.MaxHorseNameLen);
        Assert.True(M3HorseRules.IsHorseEntityName(new string('h', 120)));
        Assert.False(M3HorseRules.IsHorseEntityName(new string('h', 121)));
    }

    [Fact]
    public void FormatOwnHorseAsk_and_TryParseOwnHorseAsk_round_trip_and_fit_QuestTextMax()
    {
        // Clear/no horse
        Assert.Equal("-", M3HorseRules.FormatOwnHorseAsk(null));
        Assert.Equal("-", M3HorseRules.FormatOwnHorseAsk("-"));
        Assert.True(M3HorseRules.TryParseOwnHorseAsk("-", out var noneHorse, out var noneTack));
        Assert.Null(noneHorse);
        Assert.Empty(noneTack);

        // Bracketed horse + unsorted duplicate tack GUIDs -> normalized sorted CSV
        const string bracketHorse = "horse[animalcare/horseCare6_ff15a088-7332-4a17-9536-3e8f6a8898ad]";
        string formatted = M3HorseRules.FormatOwnHorseAsk(bracketHorse, [SaddleCls.ToUpperInvariant(), BridleCls, SaddleCls]);
        Assert.Equal($"{bracketHorse} {BridleCls},{SaddleCls}", formatted);
        Assert.True(formatted.Length <= Protocol.QuestTextMax);

        Assert.True(M3HorseRules.TryParseOwnHorseAsk(formatted, out var parsedHorse, out var parsedTack));
        Assert.Equal(bracketHorse, parsedHorse);
        Assert.Equal([BridleCls, SaddleCls], parsedTack);

        // Worst-case length (120-char name + 16 GUIDs) fits QuestAskUp (Protocol.QuestTextMax = 1400)
        var maxGuids = Enumerable.Range(1, 20).Select(i => new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1).ToString("D")).ToList();
        string worst = M3HorseRules.FormatOwnHorseAsk(new string('h', 120), maxGuids);
        Assert.True(worst.Length <= Protocol.QuestTextMax);
        var upBytes = new LootMsg(Protocol.QuestAskOwnHorse, 1u, worst).BuildUp(Protocol.QuestAskUp, Protocol.JoinTargetHost);
        Assert.NotEmpty(upBytes);

        // Malformed inputs rejected
        Assert.False(M3HorseRules.TryParseOwnHorseAsk("", out _, out _));
        Assert.False(M3HorseRules.TryParseOwnHorseAsk("kcd2mp_horse_1", out _, out _));
        Assert.False(M3HorseRules.TryParseOwnHorseAsk("tsem_sedivka not-a-guid", out _, out _));
        Assert.False(M3HorseRules.TryParseOwnHorseAsk("tsem_sedivka 00000000-0000-0000-0000-000000000000", out _, out _));
    }

    [Fact]
    public void TryParsePeerHorseContextEvent_validates_bracketed_horse_and_flag()
    {
        const string bracketHorse = "horse[animalcare/horseCare6_ff15a088-7332-4a17-9536-3e8f6a8898ad]";
        Assert.True(M3HorseRules.TryParsePeerHorseContextEvent($"{bracketHorse} 1", out var h1, out bool on1));
        Assert.Equal(bracketHorse, h1);
        Assert.True(on1);

        Assert.True(M3HorseRules.TryParsePeerHorseContextEvent("tsem_horseForSale_2 0", out var h2, out bool on2));
        Assert.Equal("tsem_horseForSale_2", h2);
        Assert.False(on2);

        Assert.False(M3HorseRules.TryParsePeerHorseContextEvent("kcd2mp_peerhorse_1 1", out _, out _));
        Assert.False(M3HorseRules.TryParsePeerHorseContextEvent("tsem_sedivka 2", out _, out _));
    }
}
