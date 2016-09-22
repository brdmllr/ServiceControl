﻿namespace ServiceControl.Operations.Audit
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Metrics;
    using NServiceBus;
    using NServiceBus.Logging;
    using Raven.Abstractions.Data;
    using Raven.Client;
    using ServiceControl.Operations.BodyStorage;

    class ProcessAudits
    {
        private const int BATCH_SIZE = 128;
        private const int NUM_CONCURRENT_BATCHES = 5;


        private ILog logger = LogManager.GetLogger<ProcessAudits>();

        private readonly IDocumentStore store;
        private Task task;
        private volatile bool stop;
        private readonly Meter meter = Metric.Meter("Audit messages processed", Unit.Custom("Messages"));

        AuditIngestionCache auditIngestionCache;
        ProcessedMessageFactory processedMessageFactory;
        private readonly CriticalError criticalError;
        private RepeatedFailuresOverTimeCircuitBreaker breaker;

        public ProcessAudits(IDocumentStore store, AuditIngestionCache auditIngestionCache, ProcessedMessageFactory processedMessageFactory, CriticalError criticalError)
        {
            this.store = store;
            this.auditIngestionCache = auditIngestionCache;

            this.processedMessageFactory = processedMessageFactory;
            this.criticalError = criticalError;
        }

        public void Start()
        {
            breaker = new RepeatedFailuresOverTimeCircuitBreaker("ProcessAudits", TimeSpan.FromMinutes(2), ex =>
                {
                    stop = true;
                    criticalError.Raise("Repeated failures when processing audits.", ex);
                },
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
                    logger.Warn("ProcessAudits failed, having a break for 2 seconds before trying again.", ex);
                    await breaker.Failure(ex).ConfigureAwait(false);
                    logger.Warn("Restarting ProcessAudits.");
                }
            } while (!stop);
        }

        private async Task Process()
        {
            do
            {
                var tasks = auditIngestionCache.GetBatch(BATCH_SIZE * NUM_CONCURRENT_BATCHES).ToArray()
                    .Chunk(BATCH_SIZE)
                    .Select(x => Task.Run(() => HandleBatch(x.ToArray())))
                    .ToArray();

                await Task.WhenAll(tasks).ConfigureAwait(false);

                if (!stop && tasks.Length == 0)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }

                breaker.Success();
            } while (!stop);
        }

        private async Task HandleBatch(string[] files)
        {
            try
            {
                var processedFiles = new List<string>(BATCH_SIZE);
                var bulkInsert = store.BulkInsert(options: new BulkInsertOptions
                {
                    WriteTimeoutMilliseconds = 2000
                });

                foreach (var entry in files)
                {
                    Dictionary<string, string> headers;
                    ClaimsCheck bodyStorageClaimsCheck;

                    if (auditIngestionCache.TryGet(entry, out headers, out bodyStorageClaimsCheck))
                    {
                        var processedMessage = processedMessageFactory.Create(headers);
                        processedMessageFactory.AddBodyDetails(processedMessage, bodyStorageClaimsCheck);

                        bulkInsert.Store(processedMessage);

                        processedFiles.Add(entry);
                    }
                }

                if (processedFiles.Count > 0)
                {
                    await bulkInsert.DisposeAsync().ConfigureAwait(false);

                    foreach (var file in processedFiles)
                    {
                        File.Delete(file);
                    }
                }

                meter.Mark(processedFiles.Count);
            }
            catch (Exception ex)
            {
                logger.Error("Something went wrong ingesting a batch", ex);
            }
        }
    }

    public static class Ext
    {
        public static IEnumerable<T[]> Chunk<T>(this IEnumerable<T> source, int n)
        {
            var list = new List<T>(n);
            var i = 0;
            foreach (var item in source)
            {
                list.Add(item);
                if (++i == n)
                {
                    yield return list.ToArray();
                    i = 0;
                    list.Clear();
                }
            }

            if (i != 0)
            {
                yield return list.ToArray();
            }
        }
    }
}