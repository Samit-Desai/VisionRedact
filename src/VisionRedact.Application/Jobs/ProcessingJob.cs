using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Domain.Jobs;

namespace VisionRedact.Application.Jobs;

public readonly record struct JobProgress(long Completed, long Total, string Message)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}

// Error is only set for Failed
public sealed class JobStateChangedEventArgs(ProcessingJobState previous, ProcessingJobState current, Exception? error) : EventArgs
{
    public ProcessingJobState Previous { get; } = previous;

    public ProcessingJobState Current { get; } = current;

    public Exception? Error { get; } = error;
}

// watch a job and cancel it. only ProcessingJobManager runs one (RunAsync is internal)
public interface IProcessingJob
{
    Guid Id { get; }

    string Name { get; }

    ProcessingJobState State { get; }

    JobProgress Progress { get; }

    Exception? Error { get; }

    // completes when the job ends, never faults
    Task Completion { get; }

    // a handful per job. raised on the thread that made the change: the worker, or the caller of Cancel
    // (-> Cancelling, or -> Cancelled before it started). so handlers Post to the UI
    event EventHandler<JobStateChangedEventArgs>? StateChanged;

    // raised on the worker, throttled (see ReportProgress)
    event EventHandler<JobProgress>? ProgressChanged;

    /// <summary>Asks the job to stop. Any thread, any time, as often as you like.</summary>
    void Cancel();
}

// base of every background job: scan, identify people, region tracking, export, timeline previews.
// a subclass only writes ExecuteAsync. this class does the states, Cancel, errors and the progress throttle.
// Created -> Running -> Completed / Failed. Cancel before the start = Cancelled at once, while running = Cancelling,
// then Cancelled (or Completed / Failed if the work ends first). each change is one CompareExchange, so when Cancel
// and the end of the work race, exactly one wins
public abstract class ProcessingJob : IProcessingJob
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly CancellationTokenSource _cancellation = new();

    // RunContinuationsAsynchronously: whoever awaits Completion never runs inside our TrySetResult
    // (e.g. inside Cancel on the UI thread)
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _timeProvider;

    // ProcessingJobState as an int, so Interlocked.CompareExchange can swap it
    private int _state = (int)ProcessingJobState.Created;

    // reports can come from different threads over a job's life (e.g. an export stage task, then the job thread)
    private long _lastProgressTimestamp;
    private JobProgress _progress;

    protected ProcessingJob(string name, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        Name = name;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Logger = logger ?? NullLogger.Instance;
    }

    public event EventHandler<JobStateChangedEventArgs>? StateChanged;

    public event EventHandler<JobProgress>? ProgressChanged;

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; }

    public ProcessingJobState State => (ProcessingJobState)Volatile.Read(ref _state);

    public JobProgress Progress => _progress;

    public Exception? Error { get; private set; }

    public Task Completion => _completion.Task;

    protected ILogger Logger { get; }

    public void Cancel()
    {
        // not started yet: straight to Cancelled, and RunAsync will do nothing
        if (TryTransition(ProcessingJobState.Created, ProcessingJobState.Cancelled))
        {
            _completion.TrySetResult();
            return;
        }

        // running: Cancelling + trip the token, the work stops at its next check.
        // anything else (already canceling, or done): nothing to do, so calling it again is harmless
        if (TryTransition(ProcessingJobState.Running, ProcessingJobState.Cancelling))
        {
            _cancellation.Cancel();
        }
    }

    // the manager calls this once, on a pool thread
    internal async Task RunAsync()
    {
        if (!TryTransition(ProcessingJobState.Created, ProcessingJobState.Running))
        {
            _completion.TrySetResult(); // canceled before it started
            return;
        }

        // returned -> Completed. our own token canceled -> Cancelled. anything else -> Failed
        var token = _cancellation.Token;
        try
        {
            await ExecuteAsync(token).ConfigureAwait(false);
            Finish(ProcessingJobState.Completed, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Finish(ProcessingJobState.Cancelled, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Job {Job} failed.", Name);
            Finish(ProcessingJobState.Failed, ex);
        }
        finally
        {
            // Completion must never hang, whatever happened above. Try is only defensive here: Cancel sets it
            // just in the Created case, and then RunAsync already returned at the top
            _completion.TrySetResult();
        }
    }

    // the real work. check the token often
    protected abstract Task ExecuteAsync(CancellationToken cancellationToken);

    // keeps the latest value, but raises ProgressChanged at most once per ProgressInterval unless forced.
    // a scan reports on every frame, the UI would drown otherwise
    protected void ReportProgress(long completed, long total, string message, bool force = false)
    {
        _progress = new JobProgress(completed, total, message);
        var now = _timeProvider.GetTimestamp();

        var last = Interlocked.Read(ref _lastProgressTimestamp);
        if (!force && last != 0 && _timeProvider.GetElapsedTime(last, now) < ProgressInterval)
        {
            return;
        }

        Interlocked.Exchange(ref _lastProgressTimestamp, now);
        ProgressChanged?.Invoke(this, _progress);
    }

    // terminal state + one last progress event w/ the final numbers
    private void Finish(ProcessingJobState outcome, Exception? error)
    {
        Error = error; // before the state change, so a StateChanged handler already sees it

        // from Running, or from Cancelling when the work ended before it saw the token
        if (!TryTransition(ProcessingJobState.Running, outcome, error))
        {
            TryTransition(ProcessingJobState.Cancelling, outcome, error);
        }

        ProgressChanged?.Invoke(this, _progress);
    }

    // rule check first (JobStateMachine), then the CompareExchange: it writes `to` only if _state is still `from`.
    // false = not allowed, or another thread changed it first. no lock needed
    private bool TryTransition(ProcessingJobState from, ProcessingJobState to, Exception? error = null)
    {
        if (!JobStateMachine.CanTransition(from, to)
            || Interlocked.CompareExchange(ref _state, (int)to, (int)from) != (int)from)
        {
            return false;
        }

        try
        {
            StateChanged?.Invoke(this, new JobStateChangedEventArgs(from, to, error));
        }
        catch (Exception ex)
        {
            // a buggy handler must not break the state bookkeeping
            Logger.LogError(ex, "A job StateChanged handler threw.");
        }

        return true;
    }
}
