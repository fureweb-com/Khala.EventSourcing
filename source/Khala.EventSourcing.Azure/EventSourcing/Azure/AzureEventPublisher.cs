﻿namespace Khala.EventSourcing.Azure
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Messaging;
    using Microsoft.WindowsAzure.Storage.Table;
    using static Microsoft.WindowsAzure.Storage.Table.QueryComparisons;
    using static Microsoft.WindowsAzure.Storage.Table.TableOperators;
    using static Microsoft.WindowsAzure.Storage.Table.TableQuery;

    public class AzureEventPublisher : IAzureEventPublisher
    {
        private readonly CloudTable _eventTable;
        private readonly IMessageSerializer _serializer;
        private readonly IMessageBus _messageBus;

        public AzureEventPublisher(
            CloudTable eventTable,
            IMessageSerializer serializer,
            IMessageBus messageBus)
        {
            _eventTable = eventTable ?? throw new ArgumentNullException(nameof(eventTable));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public Task PublishPendingEvents<T>(
            Guid sourceId,
            CancellationToken cancellationToken)
            where T : class, IEventSourced
        {
            if (sourceId == Guid.Empty)
            {
                throw new ArgumentException(
                    $"{sourceId} cannot be empty.", nameof(sourceId));
            }

            string pendingPartition = PendingEventTableEntity.GetPartitionKey(typeof(T), sourceId);
            return Publish(pendingPartition, cancellationToken);
        }

        private async Task Publish(
            string pendingPartition,
            CancellationToken cancellationToken)
        {
            List<PendingEventTableEntity> pendingEvents = await
                GetPendingEvents(pendingPartition, cancellationToken).ConfigureAwait(false);

            if (pendingEvents.Any())
            {
                await SendPendingEvents(pendingEvents, cancellationToken).ConfigureAwait(false);
                await DeletePendingEvents(pendingEvents, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<List<PendingEventTableEntity>> GetPendingEvents(
            string pendingPartition,
            CancellationToken cancellationToken)
        {
            var query = new TableQuery<PendingEventTableEntity>();

            string filter = GenerateFilterCondition(
                nameof(ITableEntity.PartitionKey),
                Equal,
                pendingPartition);

            return new List<PendingEventTableEntity>(await _eventTable
                .ExecuteQuery(query.Where(filter), cancellationToken)
                .ConfigureAwait(false));
        }

        private async Task SendPendingEvents(
            List<PendingEventTableEntity> pendingEvents,
            CancellationToken cancellationToken)
        {
            PendingEventTableEntity firstEvent = pendingEvents.First();

            string persistentPartition = firstEvent.PersistentPartition;

            List<EventTableEntity> persistentEvents = await
                GetPersistentEvents(persistentPartition, firstEvent.Version, cancellationToken).ConfigureAwait(false);

            var persistentVersions = new HashSet<int>(persistentEvents.Select(e => e.Version));

            var envelopes =
                from e in pendingEvents
                where persistentVersions.Contains(e.Version)
                select new Envelope(e.MessageId, e.CorrelationId, _serializer.Deserialize(e.EventJson));

            await _messageBus.SendBatch(envelopes, cancellationToken).ConfigureAwait(false);
        }

        private async Task<List<EventTableEntity>> GetPersistentEvents(
            string persistentPartition,
            int version,
            CancellationToken cancellationToken)
        {
            var query = new TableQuery<EventTableEntity>();

            string filter = CombineFilters(
                GenerateFilterCondition(
                    nameof(ITableEntity.PartitionKey),
                    Equal,
                    persistentPartition),
                And,
                GenerateFilterCondition(
                    nameof(ITableEntity.RowKey),
                    GreaterThanOrEqual,
                    EventTableEntity.GetRowKey(version)));

            return new List<EventTableEntity>(await _eventTable
                .ExecuteQuery(query.Where(filter), cancellationToken)
                .ConfigureAwait(false));
        }

        private Task DeletePendingEvents(
            List<PendingEventTableEntity> pendingEvents,
            CancellationToken cancellationToken)
        {
            var batch = new TableBatchOperation();
            pendingEvents.ForEach(batch.Delete);
            return _eventTable.ExecuteBatchAsync(batch, cancellationToken);
        }

        public async void EnqueueAll(CancellationToken cancellationToken)
            => await PublishAllEvents(cancellationToken).ConfigureAwait(false);

        [EditorBrowsable(EditorBrowsableState.Never)]
        public async Task PublishAllEvents(CancellationToken cancellationToken)
        {
            var query = new TableQuery<PendingEventTableEntity>();
            var filter = PendingEventTableEntity.ScanFilter;
            TableContinuationToken continuation = null;

            do
            {
                TableQuerySegment<PendingEventTableEntity> segment = await _eventTable
                    .ExecuteQuerySegmentedAsync(query.Where(filter), continuation, cancellationToken)
                    .ConfigureAwait(false);

                foreach (string partition in segment.Select(e => e.PartitionKey).Distinct())
                {
                    await Publish(partition, cancellationToken).ConfigureAwait(false);
                }

                continuation = segment.ContinuationToken;
            }
            while (continuation != null);
        }
    }
}
