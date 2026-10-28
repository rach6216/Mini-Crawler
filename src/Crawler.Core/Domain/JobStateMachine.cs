namespace Crawler.Core.Domain;

public static class JobStateMachine
{
    // Running -> Running models a re-claim after a lease expired; it is a transition in the
    // database (new owner, new lease) even though the status label does not change.
    private static readonly IReadOnlyDictionary<JobStatus, JobStatus[]> AllowedTransitions =
        new Dictionary<JobStatus, JobStatus[]>
        {
            [JobStatus.Pending] = [JobStatus.Running, JobStatus.Canceled, JobStatus.Failed],
            [JobStatus.Running] = [JobStatus.Running, JobStatus.Completed, JobStatus.Failed, JobStatus.Canceled],
            [JobStatus.Completed] = [],
            [JobStatus.Failed] = [],
            [JobStatus.Canceled] = [],
        };

    public static bool CanTransition(JobStatus from, JobStatus to) =>
        AllowedTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;
}
