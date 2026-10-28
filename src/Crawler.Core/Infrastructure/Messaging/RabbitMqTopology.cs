using RabbitMQ.Client;

namespace Crawler.Core.Infrastructure.Messaging;

/// <summary>
/// Queue layout shared by publisher and consumer. Declarations are idempotent, but RabbitMQ rejects
/// re-declaring a queue with different arguments, so changing the retry TTL requires deleting
/// <see cref="RetryQueue"/> first.
/// </summary>
public static class RabbitMqTopology
{
    public const string Exchange = "crawl";
    public const string DeadLetterExchange = "crawl.dlx";
    public const string JobsQueue = "crawl.jobs";
    public const string RetryQueue = "crawl.jobs.retry";
    public const string DeadLetterQueue = "crawl.jobs.dlq";
    public const string AttemptHeader = "x-attempt";
    public const string JsonContentType = "application/json";

    public static async Task DeclareAsync(IChannel channel, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(JobsQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(JobsQueue, Exchange, JobsQueue, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(RetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (int)retryDelay.TotalMilliseconds,
                ["x-dead-letter-exchange"] = Exchange,
                ["x-dead-letter-routing-key"] = JobsQueue,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(RetryQueue, Exchange, RetryQueue, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, string.Empty, cancellationToken: cancellationToken);
    }

    /// <summary>Reads the attempt header, tolerating the numeric encodings different clients produce.</summary>
    public static int ReadAttempt(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is null || !properties.Headers.TryGetValue(AttemptHeader, out var raw) || raw is null)
        {
            return 1;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            short s => s,
            byte b => b,
            byte[] bytes when int.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
            _ => 1,
        };
    }
}
