using Crawler.Core.Application.Options;
using Crawler.Core.Infrastructure.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Crawler.Worker.Messaging;

/// <summary>
/// Adapts RabbitMQ deliveries to <see cref="IJobMessageHandler"/> and settles them with manual acks.
/// A closed channel (broker restart, channel error) is replaced with exponential backoff instead of
/// crashing the host; unsettled deliveries are redelivered by the broker and handled idempotently.
/// </summary>
public sealed class JobConsumer(
    RabbitMqConnectionProvider connections,
    IJobMessageHandler handler,
    DatabaseReadiness databaseReadiness,
    IOptions<CrawlerOptions> options,
    TimeProvider clock,
    ILogger<JobConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(5);

    private readonly object _inFlightLock = new();
    private readonly HashSet<Task> _inFlight = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await databaseReadiness.WaitUntilReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var reconnectDelay = InitialReconnectDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilChannelClosesAsync(stoppingToken);
                reconnectDelay = InitialReconnectDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RabbitMQ consumer failed; reconnecting in {Delay}", reconnectDelay);
            }

            try
            {
                await Task.Delay(reconnectDelay, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            reconnectDelay = TimeSpan.FromTicks(Math.Min(reconnectDelay.Ticks * 2, MaxReconnectDelay.Ticks));
        }
    }

    private async Task ConsumeUntilChannelClosesAsync(CancellationToken stoppingToken)
    {
        var prefetch = (ushort)options.Value.Worker.Prefetch;
        var connection = await connections.GetConnectionAsync(stoppingToken);

        // Dispatch concurrency = prefetch so that every prefetched job is crawled in parallel.
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false,
            consumerDispatchConcurrency: prefetch);
        var channel = await connection.CreateChannelAsync(channelOptions, stoppingToken);

        // Handlers are bound to this channel: once it closes their deliveries can no longer be settled,
        // so they stop and the broker redelivers.
        using var channelCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            var closed = new TaskCompletionSource<ShutdownEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.ChannelShutdownAsync += (_, args) =>
            {
                closed.TrySetResult(args);
                return Task.CompletedTask;
            };

            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefetch, global: false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => TrackAsync(OnReceivedAsync(channel, delivery, channelCts.Token));
            await channel.BasicConsumeAsync(RabbitMqTopology.JobsQueue, autoAck: false, consumer, stoppingToken);
            logger.LogInformation("Consuming {Queue} with prefetch {Prefetch}", RabbitMqTopology.JobsQueue, prefetch);

            var shutdown = await closed.Task.WaitAsync(stoppingToken);
            logger.LogWarning("Consumer channel closed ({ReplyCode} {ReplyText}); reconnecting", shutdown.ReplyCode, shutdown.ReplyText);
        }
        finally
        {
            await channelCts.CancelAsync();
            await WaitForInFlightAsync();
            await CloseQuietlyAsync(channel);
        }
    }

    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken channelToken)
    {
        var properties = delivery.BasicProperties;
        var jobDelivery = new JobDelivery(delivery.Body, properties.MessageId, properties.CorrelationId, RabbitMqTopology.ReadAttempt(properties));

        MessageDisposition disposition;
        try
        {
            disposition = await handler.HandleAsync(jobDelivery, channelToken);
        }
        catch (Exception ex)
        {
            // Shutdown or a failed retry publish: the message must be neither acked nor dead-lettered.
            if (ex is OperationCanceledException && channelToken.IsCancellationRequested)
            {
                logger.LogInformation("Consumer stopping; message {MessageId} left for redelivery", properties.MessageId);
            }
            else
            {
                logger.LogError(ex, "Message {MessageId} could not be handled; returning it to the queue after {Delay}s", properties.MessageId, RequeueDelay.TotalSeconds);
                // A requeue is redelivered immediately; without a pause a persistent publish failure would spin.
                // The database attempt cap (TryFailExhaustedAsync) bounds how many times this can recur per job.
                try
                {
                    await Task.Delay(RequeueDelay, channelToken);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down: still hand the message back below.
                }
            }

            await SettleAsync(channel, () => channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true), properties.MessageId);
            return;
        }

        if (disposition == MessageDisposition.Ack)
        {
            await SettleAsync(channel, () => channel.BasicAckAsync(delivery.DeliveryTag, multiple: false), properties.MessageId);
        }
        else
        {
            await SettleAsync(channel, () => channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false), properties.MessageId);
        }
    }

    private async Task SettleAsync(IChannel channel, Func<ValueTask> settle, string? messageId)
    {
        try
        {
            if (channel.IsOpen)
            {
                await settle();
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not settle message {MessageId}; the broker will redeliver it", messageId);
            return;
        }

        logger.LogWarning("Channel closed before message {MessageId} was settled; the broker will redeliver it", messageId);
    }

    private async Task TrackAsync(Task handling)
    {
        lock (_inFlightLock)
        {
            _inFlight.Add(handling);
        }

        try
        {
            await handling;
        }
        finally
        {
            lock (_inFlightLock)
            {
                _inFlight.Remove(handling);
            }
        }
    }

    private async Task WaitForInFlightAsync()
    {
        Task[] pending;
        lock (_inFlightLock)
        {
            pending = [.. _inFlight];
        }

        try
        {
            await Task.WhenAll(pending);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "In-flight handler ended with an error during channel shutdown");
        }
    }

    private async Task CloseQuietlyAsync(IChannel channel)
    {
        try
        {
            if (channel.IsOpen)
            {
                await channel.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring error while closing the consumer channel");
        }
        finally
        {
            channel.Dispose();
        }
    }
}
