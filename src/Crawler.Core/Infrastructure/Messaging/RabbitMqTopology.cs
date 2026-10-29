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

    /// <summary>Attempt number of a message's first delivery; also assumed when the header is missing or unreadable.</summary>
    public const int FirstAttempt = 1;

    // RabbitMQ queue arguments (dead-lettering and per-queue message TTL).
    private const string DeadLetterExchangeArgument = "x-dead-letter-exchange";
    private const string DeadLetterRoutingKeyArgument = "x-dead-letter-routing-key";
    private const string MessageTtlArgument = "x-message-ttl";

    public static async Task DeclareAsync(IChannel channel, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(JobsQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                [DeadLetterExchangeArgument] = DeadLetterExchange,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(JobsQueue, Exchange, JobsQueue, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(RetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                [MessageTtlArgument] = (int)retryDelay.TotalMilliseconds,
                [DeadLetterExchangeArgument] = Exchange,
                [DeadLetterRoutingKeyArgument] = JobsQueue,
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
            return FirstAttempt;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            short s => s,
            byte b => b,
            byte[] bytes when int.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
            _ => FirstAttempt,
        };
    }
}
