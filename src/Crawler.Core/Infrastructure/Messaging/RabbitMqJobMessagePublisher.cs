using System.Text;
using System.Text.Json;
using Crawler.Core.Application.Messaging;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Crawler.Core.Infrastructure.Messaging;

public sealed class RabbitMqJobMessagePublisher(
    RabbitMqConnectionProvider connections,
    ILogger<RabbitMqJobMessagePublisher> logger) : IJobMessagePublisher
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(CrawlJobMessage message, int attempt, JobQueue queue, CancellationToken cancellationToken)
    {
        var routingKey = queue switch
        {
            JobQueue.Main => RabbitMqTopology.JobsQueue,
            JobQueue.Retry => RabbitMqTopology.RetryQueue,
            _ => throw new ArgumentOutOfRangeException(nameof(queue), queue, null),
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = RabbitMqTopology.JsonContentType,
            MessageId = message.MessageId.ToString(),
            CorrelationId = message.CorrelationId.ToString(),
            Headers = new Dictionary<string, object?> { [RabbitMqTopology.AttemptHeader] = attempt },
        };

        // Publisher confirms are enabled on the channel, so BasicPublishAsync completes only after the
        // broker has persisted the message (or throws).
        await using var channel = await connections.CreateChannelAsync(publisherConfirms: true, cancellationToken);
        await channel.BasicPublishAsync(RabbitMqTopology.Exchange, routingKey, mandatory: true, properties, body, cancellationToken);

        logger.LogInformation("Published {MessageType} for job {JobId} to {Queue} (attempt {Attempt}, message {MessageId})",
            nameof(CrawlJobMessage), message.JobId, routingKey, attempt, message.MessageId);
    }

    public static CrawlJobMessage? TryDeserialize(ReadOnlyMemory<byte> body)
    {
        try
        {
            var message = JsonSerializer.Deserialize<CrawlJobMessage>(body.Span, SerializerOptions);
            return message is { JobId: var jobId } && jobId != Guid.Empty && message.SchemaVersion == CrawlJobMessage.CurrentSchemaVersion
                ? message
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Describe(ReadOnlyMemory<byte> body)
    {
        const int maxPreview = 200;
        var text = Encoding.UTF8.GetString(body.Span);
        return text.Length <= maxPreview ? text : text[..maxPreview] + "…";
    }
}
