using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class WorldTextFormatTests
{
    [Theory]
    [InlineData(600, "06:00")]
    [InlineData(610, "06:10")]
    [InlineData(1200, "12:00")]
    [InlineData(1350, "13:50")]
    [InlineData(2350, "23:50")]
    [InlineData(2600, "26:00")] // the game clock runs past midnight until 2 AM (2600)
    [InlineData(0, "00:00")]
    [InlineData(5, "00:05")]
    public void FormatTime_writes_the_game_clock_as_hh_mm(int clock, string expected)
    {
        Assert.Equal(expected, WorldText.FormatTime(clock));
    }

    [Theory]
    [InlineData(0, "up")]
    [InlineData(1, "right")]
    [InlineData(2, "down")]
    [InlineData(3, "left")]
    [InlineData(4, "?")]
    [InlineData(-1, "?")]
    public void DirName_names_the_four_facing_directions(int facing, string expected)
    {
        Assert.Equal(expected, WorldText.DirName(facing));
    }
}
