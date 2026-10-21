using VisionRedact.Domain.Jobs;
using S = VisionRedact.Domain.Jobs.ProcessingJobState;

namespace VisionRedact.Domain.Tests.Jobs;

public class JobStateMachineTests
{
    [Theory]
    [InlineData(S.Created, S.Running)]
    [InlineData(S.Created, S.Cancelled)]
    [InlineData(S.Running, S.Completed)]
    [InlineData(S.Running, S.Failed)]
    [InlineData(S.Running, S.Cancelling)]
    [InlineData(S.Cancelling, S.Cancelled)]
    [InlineData(S.Cancelling, S.Completed)]
    [InlineData(S.Cancelling, S.Failed)]
    public void Legal_transitions_are_allowed(S from, S to) => Assert.True(JobStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(S.Created, S.Completed)]
    [InlineData(S.Created, S.Cancelling)]
    [InlineData(S.Running, S.Cancelled)]
    [InlineData(S.Running, S.Created)]
    [InlineData(S.Cancelling, S.Running)]
    [InlineData(S.Completed, S.Running)]
    [InlineData(S.Failed, S.Cancelled)]
    [InlineData(S.Cancelled, S.Running)]
    public void Illegal_transitions_are_rejected(S from, S to) => Assert.False(JobStateMachine.CanTransition(from, to));

    [Fact]
    public void Terminal_and_active_states()
    {
        Assert.All([S.Completed, S.Failed, S.Cancelled], s => Assert.True(JobStateMachine.IsTerminal(s)));
        Assert.All([S.Created, S.Running, S.Cancelling], s => Assert.False(JobStateMachine.IsTerminal(s)));
        Assert.True(JobStateMachine.IsActive(S.Running));
        Assert.True(JobStateMachine.IsActive(S.Cancelling));
        Assert.False(JobStateMachine.IsActive(S.Created));
    }
}
