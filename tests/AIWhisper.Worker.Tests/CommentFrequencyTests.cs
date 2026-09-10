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
        Assert.Equal(0.05d, new OpenAIOptions().CreativeSparkChance);
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
    [InlineData(0.099999, MinimumReactionLevel.Normal)]
    [InlineData(0.10, MinimumReactionLevel.Critical)]
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
    public void Prompt_IdentifiesSelectedLevel(MinimumReactionLevel level, string expected)
        => Assert.Contains(expected, MinimumReactionLevelInstruction.Create(level));

    [Fact]
    public void NormalAndCriticalPrompts_OnlyDefineTheirThreshold()
    {
        Assert.Contains("ordinary reaction threshold", MinimumReactionLevelInstruction.Create(MinimumReactionLevel.Normal));
        Assert.Contains("Default to silent", MinimumReactionLevelInstruction.Create(MinimumReactionLevel.Critical));
        Assert.Contains("Never invent", MinimumReactionLevelInstruction.Create(MinimumReactionLevel.Critical));
        Assert.DoesNotContain("potential threat", MinimumReactionLevelInstruction.Create(MinimumReactionLevel.Critical));
        Assert.DoesNotContain("party management", MinimumReactionLevelInstruction.Create(MinimumReactionLevel.Normal));
        Assert.DoesNotContain("recent concern", MinimumReactionLevelInstruction.Create(MinimumReactionLevel.Critical));
    }

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

    [Fact]
    public void Configuration_BindsCreativeSparkChance()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:CreativeSparkChance"] = "0.0333" })
            .Build();
        var options = new OpenAIOptions();
        configuration.GetSection("OpenAI").Bind(options);

        Assert.Equal(0.0333d, options.CreativeSparkChance);
    }

    [Theory]
    [InlineData(0.05, "Awakening", 0.049999, true)]
    [InlineData(0.05, "Awakening", 0.05, false)]
    [InlineData(0.05, "Instinctive", 0.00, false)]
    [InlineData(2.00, "Established", 0.99, true)]
    [InlineData(-1.0, "Established", 0.00, false)]
    public void CreativeSpark_UsesConfiguredChanceAndSkipsInstinctive(
        double chance,
        string phase,
        double roll,
        bool expected)
        => Assert.Equal(expected, CreativeSparkInstruction.ShouldApply(chance, phase, roll));

}
