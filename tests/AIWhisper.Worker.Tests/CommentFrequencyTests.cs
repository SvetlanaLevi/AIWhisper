using AIWhisper.Worker.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class CommentFrequencyTests
{
    [Fact]
    public void OpenAIOptions_DefaultsToLow()
    {
        Assert.Equal(CommentFrequency.Low, new OpenAIOptions().CommentFrequency);
        Assert.False(new OpenAIOptions().SimplifyEnglishForNonNativeSpeakers);
    }

    [Theory]
    [InlineData("Low", CommentFrequency.Low)]
    [InlineData("Medium", CommentFrequency.Medium)]
    [InlineData("High", CommentFrequency.High)]
    [InlineData("All", CommentFrequency.All)]
    public void Configuration_BindsSupportedFrequency(string configuredValue, CommentFrequency expectedFrequency)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:CommentFrequency"] = configuredValue })
            .Build();
        var options = new OpenAIOptions();
        configuration.GetSection("OpenAI").Bind(options);

        Assert.Equal(expectedFrequency, options.CommentFrequency);
    }

    [Theory]
    [InlineData(0.00, MinimumReactionLevel.Normal)]
    [InlineData(0.249999, MinimumReactionLevel.Normal)]
    [InlineData(0.25, MinimumReactionLevel.Critical)]
    [InlineData(0.99, MinimumReactionLevel.Critical)]
    public void Low_SelectsNormalInsideTwentyFivePercentRange(double roll, MinimumReactionLevel expected)
        => Assert.Equal(expected, MinimumReactionLevelSelector.Select(CommentFrequency.Low, roll));

    [Theory]
    [InlineData(0.00, MinimumReactionLevel.Normal)]
    [InlineData(0.499999, MinimumReactionLevel.Normal)]
    [InlineData(0.50, MinimumReactionLevel.Critical)]
    [InlineData(0.99, MinimumReactionLevel.Critical)]
    public void Medium_SelectsNormalInsideFiftyPercentRange(double roll, MinimumReactionLevel expected)
        => Assert.Equal(expected, MinimumReactionLevelSelector.Select(CommentFrequency.Medium, roll));

    [Theory]
    [InlineData(0.00)]
    [InlineData(0.99)]
    public void High_AlwaysSelectsNormal(double roll)
        => Assert.Equal(MinimumReactionLevel.Normal, MinimumReactionLevelSelector.Select(CommentFrequency.High, roll));

    [Theory]
    [InlineData(0.00)]
    [InlineData(0.99)]
    public void All_AlwaysSelectsNone(double roll)
        => Assert.Equal(MinimumReactionLevel.None, MinimumReactionLevelSelector.Select(CommentFrequency.All, roll));

    [Theory]
    [InlineData(MinimumReactionLevel.None, "MINIMUM REACTION LEVEL: NONE")]
    [InlineData(MinimumReactionLevel.Normal, "MINIMUM REACTION LEVEL: NORMAL")]
    [InlineData(MinimumReactionLevel.Critical, "MINIMUM REACTION LEVEL: CRITICAL")]
    public void Prompt_UsesExactTransientFormat(MinimumReactionLevel level, string expected)
        => Assert.Equal(expected, MinimumReactionLevelInstruction.Create(level));

    [Fact]
    public void Instinctive_IsNotFilteredByMinimumReactionLevel()
    {
        Assert.False(MinimumReactionLevelSelector.AppliesToPhase("Instinctive"));
        Assert.False(MinimumReactionLevelSelector.AppliesToPhase("instinctive"));
        Assert.True(MinimumReactionLevelSelector.AppliesToPhase("Awakening"));
        Assert.True(MinimumReactionLevelSelector.AppliesToPhase("Established"));
    }

    [Fact]
    public void Configuration_BindsSimpleEnglishSetting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:SimplifyEnglishForNonNativeSpeakers"] = "true" })
            .Build();
        var options = new OpenAIOptions();
        configuration.GetSection("OpenAI").Bind(options);

        Assert.True(options.SimplifyEnglishForNonNativeSpeakers);
        Assert.Contains("simple English", SimpleEnglishInstruction.Text);
        Assert.Contains("personality", SimpleEnglishInstruction.Text);
    }
}
