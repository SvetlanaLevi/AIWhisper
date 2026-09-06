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
    [InlineData("VeryLow", CommentFrequency.VeryLow, "overwhelming default")]
    [InlineData("Low", CommentFrequency.Low, "Silence is the default")]
    [InlineData("Medium", CommentFrequency.Medium, "react fairly freely")]
    [InlineData("High", CommentFrequency.High, "Comment relatively often")]
    public void Configuration_BindsFrequencyAndGeneratesItsInstruction(
        string configuredValue,
        CommentFrequency expectedFrequency,
        string expectedInstructionFragment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:CommentFrequency"] = configuredValue,
            })
            .Build();
        var options = new OpenAIOptions();
        configuration.GetSection("OpenAI").Bind(options);

        Assert.Equal(expectedFrequency, options.CommentFrequency);
        Assert.Contains(expectedInstructionFragment, CommentFrequencyInstruction.Create(options.CommentFrequency));
    }

    [Fact]
    public void Configuration_BindsSimpleEnglishSetting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:SimplifyEnglishForNonNativeSpeakers"] = "true",
            })
            .Build();
        var options = new OpenAIOptions();
        configuration.GetSection("OpenAI").Bind(options);

        Assert.True(options.SimplifyEnglishForNonNativeSpeakers);
        Assert.Contains("simple English", SimpleEnglishInstruction.Text);
        Assert.Contains("personality", SimpleEnglishInstruction.Text);
    }
}
