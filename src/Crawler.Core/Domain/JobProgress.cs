namespace Crawler.Core.Domain;

public static class JobProgress
{
    /// <summary>
    /// Terminal jobs report 100 regardless of counters. While running, the percentage is relative to
    /// the pages that currently hold a slot, so it can dip when a level discovers many new pages.
    /// </summary>
    public static int Percent(JobStatus status, int processedPages, int discoveredPages)
    {
        if (JobStateMachine.IsTerminal(status))
        {
            return 100;
        }

        if (discoveredPages <= 0 || processedPages <= 0)
        {
            return 0;
        }

        return Math.Min(100, (int)((long)processedPages * 100 / discoveredPages));
    }
}
