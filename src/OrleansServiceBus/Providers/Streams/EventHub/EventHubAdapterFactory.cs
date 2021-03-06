﻿
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using OrleansServiceBus.Providers.Streams.EventHub;

namespace Orleans.ServiceBus.Providers
{
    public class EventHubAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache
    {
        private Logger logger;
        private IServiceProvider serviceProvider;
        private EventHubStreamProviderConfig adapterConfig;
        private IEventHubSettings hubSettings;
        private ICheckpointerSettings checkpointerSettings;
        private EventHubQueueMapper streamQueueMapper;
        private string[] partitionIds;
        private ConcurrentDictionary<QueueId, EventHubAdapterReceiver> receivers;
        private EventHubClient client;

        public string Name { get { return adapterConfig.StreamProviderName; } }
        public bool IsRewindable { get { return true; } }
        public StreamProviderDirection Direction { get { return StreamProviderDirection.ReadWrite; } }

        protected Func<IStreamQueueCheckpointer<string>, IEventHubQueueCache> CacheFactory { get; set; }
        protected Func<string, Task<IStreamQueueCheckpointer<string>>> CheckpointerFactory { get; set; }
        protected Func<string, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { get; set; }
        
        /// <summary>
        /// Factory initialization.
        /// Provider config must contain the event hub settings type or the settings themselves.
        /// EventHubSettingsType is recommended for consumers that do not want to include secure information in the cluster configuration.
        /// </summary>
        /// <param name="providerConfig"></param>
        /// <param name="providerName"></param>
        /// <param name="log"></param>
        /// <param name="svcProvider"></param>
        public void Init(IProviderConfiguration providerConfig, string providerName, Logger log, IServiceProvider svcProvider)
        {
            if (providerConfig == null) throw new ArgumentNullException("providerConfig");
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException("providerName");
            if (log == null) throw new ArgumentNullException("log");
            if (svcProvider == null) throw new ArgumentNullException("svcProvider");

            logger = log;
            serviceProvider = svcProvider;
            receivers = new ConcurrentDictionary<QueueId, EventHubAdapterReceiver>();

            adapterConfig = new EventHubStreamProviderConfig(providerName);
            adapterConfig.PopulateFromProviderConfig(providerConfig);
            hubSettings = adapterConfig.GetEventHubSettings(providerConfig, serviceProvider);
            client = EventHubClient.CreateFromConnectionString(hubSettings.ConnectionString, hubSettings.Path);

            if (CheckpointerFactory == null)
            {
                checkpointerSettings = adapterConfig.GetCheckpointerSettings(providerConfig, serviceProvider);
                CheckpointerFactory = partition => EventHubCheckpointer.Create(checkpointerSettings, adapterConfig.StreamProviderName, partition);
            }
            
            if (CacheFactory == null)
            {
                var bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(adapterConfig.CacheSizeMb, pool => new FixedSizeBuffer(1 << 20, pool));
                CacheFactory = checkpointer => new DefaultEventHubQueueCache(checkpointer, bufferPool);
            }

            if (StreamFailureHandlerFactory == null)
            {
                //TODO: Add a queue specific default failure handler with reasonable error reporting.
                StreamFailureHandlerFactory = partition => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }
        }

        public async Task<IQueueAdapter> CreateAdapter()
        {
            if (streamQueueMapper == null)
            {
                partitionIds = await GetPartitionIdsAsync();
                streamQueueMapper = new EventHubQueueMapper(partitionIds, adapterConfig.StreamProviderName);
            }
            return this;
        }

        public IQueueAdapterCache GetQueueAdapterCache()
        {
            return this;
        }

        public IStreamQueueMapper GetStreamQueueMapper()
        {
            //TODO: CreateAdapter must be called first.  Figure out how to safely enforce this
            return streamQueueMapper;
        }

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return StreamFailureHandlerFactory(streamQueueMapper.QueueToPartition(queueId));
        }

        public Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token,
            Dictionary<string, object> requestContext)
        {
            if (token != null)
            {
                throw new NotImplementedException("EventHub stream provider currently does not support non-null StreamSequenceToken.");
            }
            EventData eventData = EventHubBatchContainer.ToEventData(streamGuid, streamNamespace, events, requestContext);
            return client.SendAsync(eventData);
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return GetOrCreateReceiver(queueId);
        }

        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            return GetOrCreateReceiver(queueId);
        }

        private EventHubAdapterReceiver GetOrCreateReceiver(QueueId queueId)
        {
            return receivers.GetOrAdd(queueId, q => MakeReceiver(queueId));
        }

        private EventHubAdapterReceiver MakeReceiver(QueueId queueId)
        {
            var config = new EventHubPartitionConfig
            {
                Hub = hubSettings,
                Partition = streamQueueMapper.QueueToPartition(queueId),
            };
            return new EventHubAdapterReceiver(config, CacheFactory, CheckpointerFactory, logger);
        }

        public async Task<string[]> GetPartitionIdsAsync()
        {
            NamespaceManager namespaceManager = NamespaceManager.CreateFromConnectionString(hubSettings.ConnectionString);
            EventHubDescription hubDescription = await namespaceManager.GetEventHubAsync(hubSettings.Path);
            return hubDescription.PartitionIds;
        }
    }
}
