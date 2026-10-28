using Crawler.Core.Domain;

namespace Crawler.Core.Tests;

// docs/test-plan.md section 5 (S1, S2).
public sealed class JobStateMachineTests
{
    [Theory]
    [InlineData(JobStatus.Pending, JobStatus.Running)]
    [InlineData(JobStatus.Pending, JobStatus.Canceled)]
    [InlineData(JobStatus.Pending, JobStatus.Failed)]
    [InlineData(JobStatus.Running, JobStatus.Running)]
    [InlineData(JobStatus.Running, JobStatus.Completed)]
    [InlineData(JobStatus.Running, JobStatus.Failed)]
    [InlineData(JobStatus.Running, JobStatus.Canceled)]
    public void Valid_transitions_are_allowed(JobStatus from, JobStatus to)
    {
        Assert.True(JobStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(JobStatus.Pending, JobStatus.Completed)]
    [InlineData(JobStatus.Pending, JobStatus.Pending)]
    [InlineData(JobStatus.Running, JobStatus.Pending)]
    [InlineData(JobStatus.Completed, JobStatus.Running)]
    [InlineData(JobStatus.Completed, JobStatus.Canceled)]
    [InlineData(JobStatus.Completed, JobStatus.Failed)]
    [InlineData(JobStatus.Failed, JobStatus.Running)]
    [InlineData(JobStatus.Failed, JobStatus.Canceled)]
    [InlineData(JobStatus.Canceled, JobStatus.Running)]
    [InlineData(JobStatus.Canceled, JobStatus.Completed)]
    public void Invalid_transitions_are_rejected(JobStatus from, JobStatus to)
    {
        Assert.False(JobStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(JobStatus.Completed, true)]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(JobStatus.Canceled, true)]
    [InlineData(JobStatus.Pending, false)]
    [InlineData(JobStatus.Running, false)]
    public void Terminal_states_are_identified(JobStatus status, bool expected)
    {
        Assert.Equal(expected, JobStateMachine.IsTerminal(status));
    }
}
