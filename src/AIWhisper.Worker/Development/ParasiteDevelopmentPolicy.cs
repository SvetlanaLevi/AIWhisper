using AIWhisper.Worker.Configuration;

namespace AIWhisper.Worker.Development;

public sealed class ParasiteDevelopmentPolicy
{
    private readonly ParasiteDevelopmentOptions _options;

    public ParasiteDevelopmentPolicy(ParasiteDevelopmentOptions options) => _options = options;

    public bool Enabled => _options.Enabled;

    public bool EnsureInitialized(ParasiteDevelopmentState state, out string? warning)
    {
        warning = null;
        if (!string.IsNullOrWhiteSpace(state.CurrentPhase))
        {
            TryGetPhase(state.CurrentPhase, out _, out _, out warning);
            return false;
        }

        var firstConfiguredPhase = _options.PhaseSequence.FirstOrDefault(phase => !string.IsNullOrWhiteSpace(phase));
        if (!TryGetPhase(firstConfiguredPhase, out var phaseName, out _, out warning)) return false;

        state.CurrentPhase = phaseName;
        return true;
    }

    public bool TryAdvance(ParasiteDevelopmentState state, string? region, out string? previousPhase, out string? warning)
    {
        previousPhase = null;
        warning = null;
        if (!Enabled || string.IsNullOrWhiteSpace(region)) return false;

        EnsureInitialized(state, out warning);
        if (warning is not null || string.IsNullOrWhiteSpace(state.CurrentPhase)) return false;
        if (!TryGetValue(_options.Regions, region.Trim(), out var targetPhaseName)) return false;
        if (!TryGetPhase(state.CurrentPhase, out var currentName, out var currentIndex, out warning)) return false;
        if (!TryGetPhase(targetPhaseName, out var targetName, out var targetIndex, out warning)) return false;
        if (targetIndex <= currentIndex) return false;

        previousPhase = currentName;
        state.CurrentPhase = targetName;
        state.ReachedInRegion = region.Trim();
        return true;
    }

    public bool TryGetInstruction(ParasiteDevelopmentState state, out string phaseName, out string prompt)
    {
        phaseName = string.Empty;
        prompt = string.Empty;
        if (!Enabled || string.IsNullOrWhiteSpace(state.CurrentPhase)) return false;
        if (!TryGetPhase(state.CurrentPhase, out phaseName, out _, out _)) return false;
        if (!TryGetValue(_options.PhasePrompts, phaseName, out prompt) || string.IsNullOrWhiteSpace(prompt)) return false;

        prompt = prompt.Trim();
        return true;
    }

    public bool TryGetPendingIntroduction(
        ParasiteDevelopmentState state,
        out PhaseIntroductionOptions introduction)
    {
        introduction = new PhaseIntroductionOptions();
        if (!Enabled || string.IsNullOrWhiteSpace(state.CurrentPhase)) return false;
        if (!TryGetValue(_options.PhaseIntroductions, state.CurrentPhase, out var configured)) return false;
        if (string.IsNullOrWhiteSpace(configured.Id) || string.IsNullOrWhiteSpace(configured.Text)) return false;
        if (state.DeliveredOneShots.Contains(configured.Id)) return false;

        introduction = configured;
        return true;
    }

    private bool TryGetPhase(string? requestedName, out string name, out int index, out string? warning)
    {
        name = string.Empty;
        index = -1;
        warning = null;
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            warning = $"parasite development phase '{requestedName}' is not configured; development instructions are skipped";
            return false;
        }

        index = _options.PhaseSequence.FindIndex(phase => string.Equals(phase, requestedName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            warning = $"parasite development phase '{requestedName}' is not configured; development instructions are skipped";
            return false;
        }

        name = _options.PhaseSequence[index];
        return true;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, string> values, string key, out string value)
    {
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetValue<T>(IReadOnlyDictionary<string, T> values, string key, out T value)
    {
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }
}
