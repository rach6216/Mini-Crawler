using Crawler.Core.Domain;

namespace Crawler.Core.Tests;

public sealed class JobProgressTests
{
    [Theory]
    [InlineData(JobStatus.Running, 0, 0, 0)]
    [InlineData(JobStatus.Running, 5, 12, 41)]
    [InlineData(JobStatus.Running, 12, 12, 100)]
    [InlineData(JobStatus.Pending, 0, 0, 0)]
    [InlineData(JobStatus.Completed, 0, 0, 100)]
    [InlineData(JobStatus.Failed, 1, 10, 100)]
    [InlineData(JobStatus.Canceled, 3, 10, 100)]
    public void Percent_follows_contract_formula(JobStatus status, int processed, int discovered, int expected)
    {
        Assert.Equal(expected, JobProgress.Percent(status, processed, discovered));
    }
}
