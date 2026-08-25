using System.Collections.Concurrent;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Pipeline;

namespace Lanshu.Presenter.App;

public sealed class RunRecord
{
    private readonly List<object> _events = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Id { get; init; }

    public required string JobDirectory { get; init; }

    public required CancellationTokenSource Cancellation { get; init; }

    public PipelineResult? Result { get; set; }

    public bool Finished { get; private set; }

    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    public void Publish(object payload)
    {
        lock (_events)
        {
            _events.Add(payload);
            Signal();
        }
    }

    public void Complete()
    {
        lock (_events)
        {
            Finished = true;
            Signal();
        }
    }

    private void Signal()
    {
        var waiters = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waiters.TrySetResult();
    }

    /// <summary>
    /// Reads every event from <paramref name="index"/> onwards, then waits for more. Subscribers
    /// track their own position, so a page that reloads mid-run replays the backlog exactly once
    /// instead of seeing the earlier events twice.
    /// </summary>
    public async IAsyncEnumerable<object> ReadFromAsync(
        int index,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            List<object> pending;
            Task changed;
            bool finished;

            lock (_events)
            {
                pending = index < _events.Count ? _events.GetRange(index, _events.Count - index) : new List<object>();
                index = _events.Count;
                changed = _changed.Task;
                finished = Finished;
            }

            foreach (var item in pending)
            {
                yield return item;
            }

            if (finished)
            {
                yield break;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Runs one pipeline per job in the background and streams its progress to the UI.
/// </summary>
public sealed class RunManager
{
    private readonly ConcurrentDictionary<string, RunRecord> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _byJob = new(StringComparer.OrdinalIgnoreCase);
    private readonly SettingsStore _store;
    private readonly IHttpClientFactory _httpClientFactory;

    public RunManager(SettingsStore store, IHttpClientFactory httpClientFactory)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
    }

    public RunRecord? Find(string id) => _runs.TryGetValue(id, out var record) ? record : null;

    public RunRecord? FindByJob(string jobDirectory) =>
        _byJob.TryGetValue(Path.GetFullPath(jobDirectory), out var id) ? Find(id) : null;

    public RunRecord Start(JobPaths paths, PipelineOptions options)
    {
        // Never run the same job twice at once; both runs would fight over the same files.
        var existing = FindByJob(paths.Root);
        if (existing is { Finished: false })
        {
            return existing;
        }

        var record = new RunRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            JobDirectory = paths.Root,
            Cancellation = new CancellationTokenSource(),
        };

        _runs[record.Id] = record;
        _byJob[paths.Root] = record.Id;

        _ = Task.Run(async () =>
        {
            var httpClient = _httpClientFactory.CreateClient("providers");
            var pipeline = new PresenterVideoPipeline(_store, httpClient);
            var progress = new Progress<PipelineEvent>(item => record.Publish(new
            {
                type = "progress",
                stage = item.Stage,
                message = item.Message,
                progress = item.Progress,
            }));

            try
            {
                var result = await pipeline
                    .RunAsync(paths, options, progress, record.Cancellation.Token)
                    .ConfigureAwait(false);
                record.Result = result;
                record.Publish(new
                {
                    type = "done",
                    outcome = result.Outcome.ToString(),
                    message = result.Message,
                    approvalKind = result.ApprovalKind,
                    approvalRequest = result.ApprovalRequest,
                    master = result.MasterPath,
                    share = result.SharePath,
                    contactSheet = result.ContactSheetPath,
                    cover = result.CoverPath,
                    captions = result.CaptionsPath,
                    pilot = result.PilotPath,
                    duration = result.DurationSeconds,
                    qaPassed = result.QaPassed,
                    warnings = result.Warnings,
                });
            }
            catch (Exception exception)
            {
                record.Publish(new { type = "done", outcome = "Failed", message = exception.Message });
            }
            finally
            {
                record.Complete();
            }
        });

        return record;
    }

    public void Cancel(string id)
    {
        if (_runs.TryGetValue(id, out var record))
        {
            record.Cancellation.Cancel();
        }
    }

    /// <summary>Drops finished runs older than an hour so a long session does not grow forever.</summary>
    public void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var pair in _runs.ToArray())
        {
            if (pair.Value.Finished && pair.Value.Started < cutoff)
            {
                _runs.TryRemove(pair.Key, out _);
                _byJob.TryRemove(pair.Value.JobDirectory, out _);
            }
        }
    }
}
