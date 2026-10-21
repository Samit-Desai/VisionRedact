namespace VisionRedact.Domain.Jobs;

// Created -> Running -> Completed / Failed
// cancel: Created -> Cancelled, or Running -> Cancelling -> Cancelled (rules in JobStateMachine)
public enum ProcessingJobState
{
    Created = 0,
    Running,
    Cancelling,
    Completed,
    Failed,
    Cancelled,
}

// the allowed changes as data: one place to check them, easy to unit test
public static class JobStateMachine
{
    // never written after init, so any thread can read it
    private static readonly Dictionary<ProcessingJobState, ProcessingJobState[]> Allowed = new()
    {
        [ProcessingJobState.Created] = [ProcessingJobState.Running, ProcessingJobState.Cancelled],
        [ProcessingJobState.Running] = [ProcessingJobState.Completed, ProcessingJobState.Failed, ProcessingJobState.Cancelling],

        // cancel is only a request. a job that finishes before it checks the token just ends Completed (or Failed)
        [ProcessingJobState.Cancelling] = [ProcessingJobState.Cancelled, ProcessingJobState.Completed, ProcessingJobState.Failed],
        [ProcessingJobState.Completed] = [],
        [ProcessingJobState.Failed] = [],
        [ProcessingJobState.Cancelled] = [],
    };

    public static bool CanTransition(ProcessingJobState from, ProcessingJobState to) => Allowed[from].Contains(to);

    public static bool IsTerminal(ProcessingJobState state) => Allowed[state].Length == 0;

    // active = Running or Cancelling. in Cancelling the worker hasn't stopped yet
    public static bool IsActive(ProcessingJobState state) => state is ProcessingJobState.Running or ProcessingJobState.Cancelling;
}
