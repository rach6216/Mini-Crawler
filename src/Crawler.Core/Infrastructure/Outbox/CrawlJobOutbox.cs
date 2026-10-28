using System.Text.Json;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Messaging;

namespace Crawler.Core.Infrastructure.Outbox;

/// <summary>Builds and reads the outbox row that asks a worker to process a newly created job.</summary>
public static class CrawlJobOutbox
{
    public static OutboxMessage CreateRequested(CrawlJob job, DateTimeOffset now)
    {
        var message = CrawlJobMessage.For(job.Id, job.CorrelationId, now);
        var payload = JsonSerializer.Serialize(message, RabbitMqJobMessagePublisher.SerializerOptions);
        return OutboxMessage.CrawlJobRequested(job.Id, payload, now);
    }

    public static CrawlJobMessage? TryReadPayload(OutboxMessage row)
    {
        try
        {
            var message = JsonSerializer.Deserialize<CrawlJobMessage>(row.Payload, RabbitMqJobMessagePublisher.SerializerOptions);
            return message is not null && message.JobId != Guid.Empty ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
