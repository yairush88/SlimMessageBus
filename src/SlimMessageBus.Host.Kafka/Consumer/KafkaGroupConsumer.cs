using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using SlimMessageBus.Host.Collections;
using ConsumeResult = Confluent.Kafka.ConsumeResult<Confluent.Kafka.Ignore, byte[]>;
using IConsumer = Confluent.Kafka.IConsumer<Confluent.Kafka.Ignore, byte[]>;

namespace SlimMessageBus.Host.Kafka
{
    public class KafkaGroupConsumer : IDisposable, IKafkaCommitController
    {
        private readonly ILogger _logger;

        public KafkaMessageBus MessageBus { get; }
        public string Group { get; }
        public ICollection<string> Topics { get; }

        private readonly SafeDictionaryWrapper<TopicPartition, IKafkaTopicPartitionProcessor> _processors;
        private IConsumer _consumer;

        private Task _consumerTask;
        private CancellationTokenSource _consumerCts;

        public KafkaGroupConsumer(KafkaMessageBus messageBus, string group, string[] topics, Func<TopicPartition, IKafkaCommitController, IKafkaTopicPartitionProcessor> processorFactory)
        {
            MessageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            Group = group ?? throw new ArgumentNullException(nameof(group));
            Topics = topics ?? throw new ArgumentNullException(nameof(topics));

            _logger = messageBus.LoggerFactory.CreateLogger<KafkaGroupConsumer>();

            _logger.LogInformation("Creating for group: {0}, topics: {1}", group, string.Join(", ", topics));

            _processors = new SafeDictionaryWrapper<TopicPartition, IKafkaTopicPartitionProcessor>(tp => processorFactory(tp, this));

            _consumer = CreateConsumer(group);
            _consumer.OnMessage += OnMessage;
            _consumer.OnPartitionsAssigned += OnPartitionAssigned;
            _consumer.OnPartitionsRevoked += OnPartitionRevoked;
            _consumer.OnPartitionEOF += OnPartitionEndReached;
            _consumer.OnOffsetsCommitted += OnOffsetsCommitted;
            _consumer.OnStatistics += OnStatistics;
        }

        #region Implementation of IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_consumerTask != null)
                {
                    Stop();
                }

                _processors.Clear(x => x.DisposeSilently("processor", _logger));

                // dispose the consumer
                if (_consumer != null)
                {
                    _consumer.DisposeSilently("consumer", _logger);
                    _consumer = null;
                }
            }
        }

        #endregion

        protected IConsumer CreateConsumer(string group)
        {
            var config = MessageBus.ProviderSettings.ConsumerConfigFactory(group);
            config.BootstrapServers = MessageBus.ProviderSettings.BrokerList;
            config.GroupId = group;
            // ToDo: add support for auto commit
            config.EnableAutoCommit = false;
            var consumer = MessageBus.ProviderSettings.ConsumerFactory(group, config);
            return consumer;
        }

        public void Start()
        {
            if (_consumerTask != null)
            {
                throw new MessageBusException($"Consumer for group {Group} already started");
            }

            _logger.LogInformation("Group [{group}]: Subscribing to topics: {topics}", Group, string.Join(", ", Topics));
            _consumer.Subscribe(Topics);

            _consumerCts = new CancellationTokenSource();
            _consumerTask = Task.Factory.StartNew(ConsumerLoop, _consumerCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// The consumer group loop
        /// </summary>
        protected virtual async Task ConsumerLoop()
        {
            _logger.LogInformation("Group [{group}]: Consumer loop started", Group);
            try
            {
                var pollInterval = MessageBus.ProviderSettings.ConsumerPollInterval;
                var pollRetryInterval = MessageBus.ProviderSettings.ConsumerPollRetryInterval;

                for (var ct = _consumerCts.Token; !ct.IsCancellationRequested;)
                {
                    try
                    {
                        _logger.LogTrace("Group [{group}]: Polling consumer", Group);
                        var cr = _consumer.Consume(pollInterval);

                        if (cr.IsPartitionEOF)
                        {
                            await OnPartitionEndReached(cr.TopicPartitionOffset).ConfigureAwait(false);
                        }
                        else
                        {
                            await OnMessage(cr).ConfigureAwait(false);

                        }

                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Group [{group}]: Error occured while polling new messages (will retry in {retryInterval})", Group, pollRetryInterval);
                        await Task.Delay(pollRetryInterval, _consumerCts.Token).ConfigureAwait(false);
                    }
                }

                // Ensure the consumer leaves the group cleanly and final offsets are committed.
                _consumer.Close();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Group [{group}]: Error occured in group loop (terminated)", e, Group);
            }
            finally
            {
                _logger.LogInformation("Group [{group}]: Consumer loop finished", Group);
            }
        }

        public void Stop()
        {
            if (_consumerTask == null)
            {
                throw new MessageBusException($"Consumer for group {Group} not yet started");
            }

            /*
            _logger.LogInformation("Group [{group}]: Unassigning partitions", Group);
            _consumer.Unassign();

            _logger.LogInformation("Group [{group}]: Unsubscribing from topics", Group);
            _consumer.Unsubscribe();
            */

            _consumerCts.Cancel();
            try
            {
                _consumerTask.Wait();
            }
            finally
            {
                _consumerTask = null;
                _consumerCts = null;
            }
        }

        protected virtual void OnPartitionAssigned(object sender, List<TopicPartition> partitions)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Group [{group}]: Assigned partitions: {1}", Group, string.Join(", ", partitions));
            }

            // Ensure processors exist for each assigned topic-partition
            partitions.ForEach(tp => _processors.GetOrAdd(tp));

            _consumer?.Assign(partitions);
        }

        protected virtual void OnPartitionRevoked(object sender, List<TopicPartition> partitions)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Group [{0}]: Revoked partitions: {1}", Group, string.Join(", ", partitions));
            }

            partitions.ForEach(tp => _processors.Dictonary[tp].OnPartitionRevoked().Wait());

            _consumer?.Unassign();
        }

        protected virtual async ValueTask OnPartitionEndReached([NotNull] TopicPartitionOffset offset)
        {
            _logger.LogDebug("Group [{group}]: Reached end of partition: {partition}, next message will be at offset: {offset}", Group, offset.TopicPartition, offset.Offset);

            var processor = _processors.Dictonary[offset.TopicPartition];
            await processor.OnPartitionEndReached(offset).ConfigureAwait(false);
        }

        protected virtual async ValueTask OnMessage([NotNull] ConsumeResult message)
        {
            _logger.LogDebug("Group [{group}]: Received message with offset: {offset}, payload size: {messageSize}", Group, message.TopicPartitionOffset, message.Message.Value?.Length ?? 0);

            var processor = _processors.Dictonary[message.TopicPartition];
            await processor.OnMessage(message).ConfigureAwait(false);
        }

        protected virtual void OnOffsetsCommitted(object sender, CommittedOffsets e)
        {
            if (e.Error.IsError || e.Error.IsFatal)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Group [{group}]: Failed to commit offsets: [{offsets}], error: {error}", Group, string.Join(", ", e.Offsets), e.Error.Reason);
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Group [{group}]: Successfully committed offsets: [{offsets}]", Group, string.Join(", ", e.Offsets));
            }
        }

        protected virtual void OnStatistics(object sender, string e)
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Group [{0}]: Statistics: {1}", Group, e);
            }
        }

        #region Implementation of IKafkaCoordinator

        public async ValueTask Commit(TopicPartitionOffset offset)
        {
            _logger.LogDebug("Group [{0}]: Commit offset: {1}", Group, offset);
            _consumer.Commit(new[] { offset });
        }

        #endregion
    }
}