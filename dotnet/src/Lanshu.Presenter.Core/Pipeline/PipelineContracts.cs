using Lanshu.Presenter.Core.Models;

namespace Lanshu.Presenter.Core.Pipeline;

public enum PipelineOutcome
{
    Completed,
    NeedsApproval,
    Failed,
    Cancelled,
}

public sealed record PipelineEvent(string Stage, string Message, double Progress)
{
    public string Utc { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

public sealed record PipelineResult
{
    public required PipelineOutcome Outcome { get; init; }

    public required JobState State { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>Set when the run stopped on a billing or pilot gate; shown verbatim to the user.</summary>
    public string ApprovalRequest { get; init; } = string.Empty;

    public string ApprovalKind { get; init; } = string.Empty;

    public string MasterPath { get; init; } = string.Empty;

    public string SharePath { get; init; } = string.Empty;

    public string ContactSheetPath { get; init; } = string.Empty;

    public string CoverPath { get; init; } = string.Empty;

    public string CaptionsPath { get; init; } = string.Empty;

    public string PilotPath { get; init; } = string.Empty;

    public string PreviewPath { get; init; } = string.Empty;

    public double DurationSeconds { get; init; }

    public bool QaPassed { get; init; }

    public List<string> Warnings { get; init; } = new();
}

public sealed record PipelineOptions
{
    /// <summary>Stop after locking narration; used by the "preview the voice" action in the app.</summary>
    public bool AudioOnly { get; init; }

    /// <summary>
    /// Render a small, fast proxy instead of the delivery master. Skips the loudness pass,
    /// the master and share encodes, the contact sheet and the acceptance gates.
    /// </summary>
    public bool Preview { get; init; }

    /// <summary>Short edge of the proxy render, in pixels.</summary>
    public int PreviewHeight { get; init; } = 640;

    /// <summary>Re-run finished stages instead of resuming from the recorded state.</summary>
    public bool Force { get; init; }

    /// <summary>Allow overwriting existing delivery outputs.</summary>
    public bool Overwrite { get; init; }

    public string OutputStem { get; init; } = string.Empty;
}
