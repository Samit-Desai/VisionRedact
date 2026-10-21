using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Domain.Jobs;

namespace VisionRedact.Application.Jobs;

// Start: AnalysisService, PeopleService, FramePreviewService, RedactionViewModel (tracking), ShellViewModel (export).
// CancelAllAsync: VideoWorkspace when another video opens, ShellViewModel at shutdown
public interface IProcessingJobManager
{
    // TODO: nothing subscribes to JobStarted yet, ok for now
    event EventHandler<IProcessingJob>? JobStarted;

    IReadOnlyList<IProcessingJob> ActiveJobs { get; }

    /// <summary>Starts the job on the thread pool and returns right away.</summary>
    IProcessingJob Start(ProcessingJob job);

    /// <summary>Cancels every active job and waits until all of them have ended.</summary>
    Task CancelAllAsync();
}

public sealed class ProcessingJobManager : IProcessingJobManager
{
    // Start comes from the UI thread or a service's pool thread, finished jobs are removed on a pool thread.
    // a lock is the simplest thing for a tiny list
    private readonly object _gate = new();
    private readonly List<ProcessingJob> _active = [];
    private readonly ILogger _logger;

    public ProcessingJobManager(ILogger<ProcessingJobManager>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public event EventHandler<IProcessingJob>? JobStarted;

    public IReadOnlyList<IProcessingJob> ActiveJobs
    {
        get
        {
            // a copy, so callers can loop over it while jobs finish
            lock (_gate)
            {
                return _active.ToArray();
            }
        }
    }

    public IProcessingJob Start(ProcessingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            _active.Add(job);
        }

        _logger.LogInformation("Starting job {Job} ({Id}).", job.Name, job.Id);

        // Task.Run b/c jobs start w/ blocking native work (opening a decoder).
        // the task is dropped on purpose: RunAsync catches what ExecuteAsync throws, callers wait on Completion
        _ = Task.Run(job.RunAsync);

        // when it ends (any outcome): off the list, and log how it ended
        _ = job.Completion.ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    _active.Remove(job);
                }

                if (job.State == ProcessingJobState.Failed)
                {
                    // the UI shows one sentence, so the reason and stack go here. this used to be missing from the log
                    _logger.LogError(job.Error, "Job {Job} failed.", job.Name);
                }
                else
                {
                    _logger.LogInformation("Job {Job} finished: {State}.", job.Name, job.State);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        JobStarted?.Invoke(this, job);
        return job;
    }

    public async Task CancelAllAsync()
    {
        // copy first, ask each to stop, then wait until each one has really ended
        var jobs = ActiveJobs;
        foreach (var job in jobs)
        {
            job.Cancel();
        }

        await Task.WhenAll(jobs.Select(j => j.Completion)).ConfigureAwait(false);
    }
}
