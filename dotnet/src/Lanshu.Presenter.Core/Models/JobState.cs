namespace Lanshu.Presenter.Core.Models;

/// <summary>
/// Production state machine from SKILL.md. A job only advances when the evidence
/// for the next state exists on disk.
/// </summary>
public enum JobState
{
    Intake = 0,
    ContentLocked = 1,
    AudioLocked = 2,
    VisualPlanLocked = 3,
    PresenterGenerated = 4,
    CompositionChecked = 5,
    Rendered = 6,
    Verified = 7,
}

public static class JobStates
{
    private static readonly (JobState State, string Wire)[] Map =
    {
        (JobState.Intake, "intake"),
        (JobState.ContentLocked, "content_locked"),
        (JobState.AudioLocked, "audio_locked"),
        (JobState.VisualPlanLocked, "visual_plan_locked"),
        (JobState.PresenterGenerated, "presenter_generated"),
        (JobState.CompositionChecked, "composition_checked"),
        (JobState.Rendered, "rendered"),
        (JobState.Verified, "verified"),
    };

    public static string ToWire(JobState state) =>
        Map.First(entry => entry.State == state).Wire;

    public static JobState Parse(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
        {
            return JobState.Intake;
        }

        foreach (var entry in Map)
        {
            if (string.Equals(entry.Wire, wire.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return entry.State;
            }
        }

        return JobState.Intake;
    }

    public static IReadOnlyList<string> All => Map.Select(entry => entry.Wire).ToArray();
}
