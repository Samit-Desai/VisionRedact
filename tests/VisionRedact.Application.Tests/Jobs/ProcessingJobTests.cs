using VisionRedact.Application.Jobs;
using VisionRedact.Domain.Jobs;

namespace VisionRedact.Application.Tests.Jobs;

public class ProcessingJobTests
{
    private readonly ProcessingJobManager _manager = new();

    [Fact]
    public async Task Successful_job_goes_created_running_completed()
    {
        var job = new DelegateJob(_ => Task.CompletedTask);
        var states = Record(job);

        _manager.Start(job);
        await job.Completion;

        Assert.Equal([ProcessingJobState.Running, ProcessingJobState.Completed], states);
    }

    [Fact]
    public async Task Exception_fails_the_job_without_escaping()
    {
        var job = new DelegateJob(_ => throw new InvalidOperationException("boom"));

        _manager.Start(job);
        await job.Completion; // never faults

        Assert.Equal(ProcessingJobState.Failed, job.State);
        Assert.Equal("boom", job.Error!.Message);
    }

    [Fact]
    public async Task Cancel_while_running_goes_through_cancelling_to_cancelled()
    {
        var started = new TaskCompletionSource();
        var job = new DelegateJob(async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        var states = Record(job);

        _manager.Start(job);
        await started.Task;
        job.Cancel();
        job.Cancel(); // twice is fine
        await job.Completion;

        Assert.Equal([ProcessingJobState.Running, ProcessingJobState.Cancelling, ProcessingJobState.Cancelled], states);
    }

    [Fact]
    public async Task Cancel_before_start_never_executes_the_work()
    {
        var executed = false;
        var job = new DelegateJob(_ =>
        {
            executed = true;
            return Task.CompletedTask;
        });

        job.Cancel();
        _manager.Start(job);
        await job.Completion;

        Assert.False(executed);
        Assert.Equal(ProcessingJobState.Cancelled, job.State);
    }

    [Fact]
    public async Task Work_that_finishes_before_observing_cancellation_completes()
    {
        var release = new TaskCompletionSource();
        var job = new DelegateJob(async _ => await release.Task); // ignores the token on purpose

        _manager.Start(job);
        await WaitFor(() => job.State == ProcessingJobState.Running);
        job.Cancel();
        release.SetResult();
        await job.Completion;

        Assert.Equal(ProcessingJobState.Completed, job.State); // Cancelling -> Completed is allowed
    }

    [Fact]
    public async Task Cancellation_from_a_foreign_token_is_a_failure_not_a_cancellation()
    {
        var job = new DelegateJob(_ => throw new OperationCanceledException(new CancellationToken(true)));

        _manager.Start(job);
        await job.Completion;

        Assert.Equal(ProcessingJobState.Failed, job.State);
    }

    [Fact]
    public async Task Manager_cancels_and_awaits_all_active_jobs()
    {
        var jobs = Enumerable.Range(0, 3).Select(_ => new DelegateJob(ct => Task.Delay(Timeout.Infinite, ct))).ToArray();
        foreach (var job in jobs)
        {
            _manager.Start(job);
        }

        await WaitFor(() => jobs.All(j => j.State == ProcessingJobState.Running));
        await _manager.CancelAllAsync();

        Assert.All(jobs, j => Assert.Equal(ProcessingJobState.Cancelled, j.State));
        await WaitFor(() => _manager.ActiveJobs.Count == 0);
    }

    [Fact]
    public async Task Progress_notifications_are_throttled()
    {
        var job = new DelegateJob((self, _) =>
        {
            for (var i = 0; i < 100_000; i++)
            {
                self.Report(i, 100_000);
            }

            return Task.CompletedTask;
        });
        var notifications = 0;
        job.ProgressChanged += (_, _) => Interlocked.Increment(ref notifications);

        _manager.Start(job);
        await job.Completion;

        Assert.InRange(notifications, 1, 50);
        Assert.Equal(99_999, job.Progress.Completed);
    }

    private static List<ProcessingJobState> Record(IProcessingJob job)
    {
        var states = new List<ProcessingJobState>();
        job.StateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.Current);
            }
        };
        return states;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(5);
        }
    }

    private sealed class DelegateJob(Func<DelegateJob, CancellationToken, Task> work) : ProcessingJob("test job")
    {
        public DelegateJob(Func<CancellationToken, Task> work)
            : this((_, ct) => work(ct))
        {
        }

        public void Report(long done, long total) => ReportProgress(done, total, "working");

        protected override Task ExecuteAsync(CancellationToken cancellationToken) => work(this, cancellationToken);
    }
}
