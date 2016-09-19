namespace ServiceControl.Operations.Error
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Metrics;
    using NServiceBus;
    using NServiceBus.CircuitBreakers;
    using NServiceBus.Logging;
    using Raven.Abstractions.Commands;
    using Raven.Client;
    using ServiceControl.Contracts.MessageFailures;
    using ServiceControl.Contracts.Operations;
    using ServiceControl.Operations.BodyStorage;

    class ProcessErrors
    {
        private const int BATCH_SIZE = 128;

        private ILog logger = LogManager.GetLogger<ProcessErrors>();

        private readonly IDocumentStore store;
        private readonly ErrorIngestionCache errorIngestionCache;
        private readonly IBus bus;
        private Task task;
        private PatchCommandDataFactory patchCommandDataFactory;
        private volatile bool stop;
        private readonly Meter meter = Metric.Meter("Error messages processed", Unit.Custom("Messages"));
        private readonly CriticalError criticalError;
        private readonly IEnumerable<IMonitorImportBatches> batchMonitors;
        private RepeatedFailuresOverTimeCircuitBreaker breaker;

        public ProcessErrors(IDocumentStore store, ErrorIngestionCache errorIngestionCache, PatchCommandDataFactory patchCommandDataFactory, IBus bus, CriticalError criticalError, IEnumerable<IMonitorImportBatches> batchMonitors)
        {
            this.store = store;
            this.errorIngestionCache = errorIngestionCache;
            this.patchCommandDataFactory = patchCommandDataFactory;
            this.bus = bus;
            this.criticalError = criticalError;
            this.batchMonitors = batchMonitors;
        }

        public void Start()
        {
            breaker = new RepeatedFailuresOverTimeCircuitBreaker("ProcessErrors", TimeSpan.FromMinutes(2), ex => criticalError.Raise("Repeated failures when processing errors.", ex),
                TimeSpan.FromSeconds(2));
            stop = false;
            task = ProcessWithRetries();
        }

        public void Stop()
        {
            stop = true;
            task.Wait();
            breaker.Dispose();
        }

        private async Task ProcessWithRetries()
        {
            do
            {
                try
                {
                    await Process().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Warn("ProcessErrors failed, having a break for 2 seconds before trying again.", ex);
                    breaker.Failure(ex);
                }
            } while (!stop);
        }

        private async Task Process()
        {
            do
            {
                var processedFiles = new List<string>(BATCH_SIZE);
                var batchMetadata = new List<Dictionary<string, object>>(BATCH_SIZE);
                var patches = new List<PatchCommandData>();
                var events = new List<object>();

                foreach (var entry in errorIngestionCache.GetBatch(BATCH_SIZE))
                {
                    if (stop)
                    {
                        break;
                    }

                    Dictionary<string, string> headers;
                    ClaimsCheck bodyStorageClaimsCheck;
                    bool recoverable;

                    if (errorIngestionCache.TryGet(entry, out headers, out recoverable, out bodyStorageClaimsCheck))
                    {
                        FailureDetails failureDetails;
                        string uniqueId;
                        Dictionary<string, object> messageMetadata;
                        var processedMessage = patchCommandDataFactory.Create(headers, recoverable, bodyStorageClaimsCheck, out failureDetails, out uniqueId, out messageMetadata);

                        patches.Add(processedMessage);
                        batchMetadata.Add(messageMetadata);
                        processedFiles.Add(entry);

                        string failedMessageId;
                        if (headers.TryGetValue("ServiceControl.Retry.UniqueMessageId", out failedMessageId))
                        {
                            events.Add(new MessageFailedRepeatedly
                            {
                                FailureDetails = failureDetails,
                                EndpointId = Address.Parse(failureDetails.AddressOfFailingEndpoint).Queue,
                                FailedMessageId = failedMessageId
                            });
                        }
                        else
                        {
                            events.Add(new MessageFailed
                            {
                                FailureDetails = failureDetails,
                                EndpointId = Address.Parse(failureDetails.AddressOfFailingEndpoint).Queue,
                                FailedMessageId = uniqueId
                            });
                        }
                    }
                }

                if (patches.Count > 0)
                {
                    await store.AsyncDatabaseCommands.BatchAsync(patches).ConfigureAwait(false);
                }

                if (batchMetadata.Count > 0)
                {
                    foreach (var batchMonitor in batchMonitors)
                    {
                        batchMonitor.Accept(batchMetadata);
                    }
                }

                if (events.Count > 0)
                {
                    Parallel.ForEach(events, e => bus.Publish(e));
                }

                foreach (var file in processedFiles)
                {
                    File.Delete(file);
                }

                meter.Mark(processedFiles.Count);

                breaker.Success();

                if (!stop && processedFiles.Count < BATCH_SIZE)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            } while (!stop);
        }
    }
}