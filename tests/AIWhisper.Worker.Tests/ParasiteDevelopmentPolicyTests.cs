using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Development;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class ParasiteDevelopmentPolicyTests
{
    [Fact]
    public void NewOrExistingCampaignWithoutState_InitializesToFirstConfiguredPhase()
    {
        var state = new ParasiteDevelopmentState();

        Assert.True(CreatePolicy().EnsureInitialized(state, out var warning));
        Assert.Null(warning);
        Assert.Equal("Instinctive", state.CurrentPhase);
    }

    [Theory]
    [InlineData("WLD_Main_A", "Instinctive", "Awakening")]
    [InlineData("SCL_Main_A", "Awakening", "Established")]
    [InlineData("CTY_Main_A", "Instinctive", "Established")]
    public void MappedRegion_AdvancesMonotonically(string region, string currentPhase, string expectedPhase)
    {
        var state = new ParasiteDevelopmentState { CurrentPhase = currentPhase };

        Assert.True(CreatePolicy().TryAdvance(state, region, out var previous, out var warning));
        Assert.Equal(currentPhase, previous);
        Assert.Null(warning);
        Assert.Equal(expectedPhase, state.CurrentPhase);
        Assert.Equal(region, state.ReachedInRegion);
    }

    [Fact]
    public void EarlierOrUnknownOrEmptyRegion_DoesNotRegressOrChangePhase()
    {
        var policy = CreatePolicy();
        var state = new ParasiteDevelopmentState { CurrentPhase = "Established" };

        Assert.False(policy.TryAdvance(state, "WLD_Main_A", out _, out _));
        Assert.False(policy.TryAdvance(state, "unknown", out _, out _));
        Assert.False(policy.TryAdvance(state, null, out _, out _));
        Assert.Equal("Established", state.CurrentPhase);
    }

    [Fact]
    public void RegionMatching_IsCaseInsensitive()
    {
        var state = new ParasiteDevelopmentState { CurrentPhase = "Instinctive" };

        Assert.True(CreatePolicy().TryAdvance(state, "wld_main_a", out _, out _));
        Assert.Equal("Awakening", state.CurrentPhase);
    }

    [Fact]
    public void DisabledPolicy_StillInitializesStateButDoesNotProvideInstructions()
    {
        var options = CreateOptions();
        options.Enabled = false;
        var state = new ParasiteDevelopmentState();
        var policy = new ParasiteDevelopmentPolicy(options);

        Assert.True(policy.EnsureInitialized(state, out _));
        Assert.False(policy.TryGetInstruction(state, out _, out _));
        Assert.Equal("Instinctive", state.CurrentPhase);
    }

    [Fact]
    public void MissingPrompt_DoesNotPreventDefaultPhaseFromBeingStored()
    {
        var options = CreateOptions();
        options.PhasePrompts.Clear();
        var state = new ParasiteDevelopmentState();
        var policy = new ParasiteDevelopmentPolicy(options);

        Assert.True(policy.EnsureInitialized(state, out var warning));
        Assert.Null(warning);
        Assert.Equal("Instinctive", state.CurrentPhase);
        Assert.False(policy.TryGetInstruction(state, out _, out _));
    }

    [Fact]
    public void InvalidConfiguredPhase_FailsSafelyWithoutChangingState()
    {
        var options = CreateOptions();
        options.Regions["TEST"] = "Missing";
        var state = new ParasiteDevelopmentState { CurrentPhase = "Instinctive" };

        Assert.False(new ParasiteDevelopmentPolicy(options).TryAdvance(state, "TEST", out _, out var warning));
        Assert.Contains("not configured", warning);
        Assert.Equal("Instinctive", state.CurrentPhase);
    }

    [Fact]
    public void PersistedPhaseMissingFromConfiguration_IsReportedWithoutResettingIt()
    {
        var state = new ParasiteDevelopmentState { CurrentPhase = "RemovedPhase" };

        Assert.False(CreatePolicy().EnsureInitialized(state, out var warning));
        Assert.Contains("RemovedPhase", warning);
        Assert.Equal("RemovedPhase", state.CurrentPhase);
    }

    [Fact]
    public void PendingIntroduction_IsReturnedOnceAndMatchedCaseInsensitively()
    {
        var options = CreateOptions();
        options.PhaseIntroductions["Awakening"] = new PhaseIntroductionOptions
        {
            Id = "awakening-intro",
            Text = "Something is wrong.",
        };
        var policy = new ParasiteDevelopmentPolicy(options);
        var state = new ParasiteDevelopmentState { CurrentPhase = "awakening" };

        Assert.True(policy.TryGetPendingIntroduction(state, out var introduction));
        Assert.Equal("awakening-intro", introduction.Id);

        state.DeliveredOneShots.Add("AWAKENING-INTRO");

        Assert.False(policy.TryGetPendingIntroduction(state, out _));
    }

    private static ParasiteDevelopmentPolicy CreatePolicy() => new(CreateOptions());

    private static ParasiteDevelopmentOptions CreateOptions() => new()
    {
        PhaseSequence = ["Instinctive", "Awakening", "Established"],
        Regions = new Dictionary<string, string>
        {
            ["WLD_Main_A"] = "Awakening",
            ["SCL_Main_A"] = "Established",
            ["CTY_Main_A"] = "Established",
        },
        PhasePrompts =
        {
            ["Instinctive"] = "instinct prompt",
            ["Awakening"] = "awakening prompt",
            ["Established"] = "established prompt",
        },
    };
}
