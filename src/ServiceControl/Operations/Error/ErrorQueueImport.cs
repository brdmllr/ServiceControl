namespace ServiceControl.Operations
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Metrics;
    using NServiceBus;
    using NServiceBus.Faults;
    using NServiceBus.Logging;
    using NServiceBus.ObjectBuilder;
    using NServiceBus.Satellites;
    using NServiceBus.Transports;
    using NServiceBus.Unicast;
    using NServiceBus.Unicast.Transport;
    using Raven.Abstractions.Commands;
    using Raven.Client;
    using ServiceBus.Management.Infrastructure.Settings;
    using ServiceControl.Contracts.MessageFailures;
    using ServiceControl.Contracts.Operations;
    using ServiceControl.EventLog;
    using ServiceControl.EventLog.Definitions;
    using ServiceControl.Infrastructure;
    using ServiceControl.MessageFailures.Handlers;
    using ServiceControl.Operations.BodyStorage;
    using ServiceControl.Operations.Error;
    using ServiceControl.Recoverability;
    using Timer = Metrics.Timer;

    public class ErrorQueueImport : IAdvancedSatellite, IDisposable
    {
        static readonly ILog Logger = LogManager.GetLogger(typeof(ErrorQueueImport));

        private readonly IBuilder builder;
        private readonly ISendMessages forwarder;
        private readonly CriticalError criticalError;
        private readonly LoggingSettings loggingSettings;
        private readonly Settings settings;
        private readonly IMessageBodyStore messageBodyStore;
        private SatelliteImportFailuresHandler satelliteImportFailuresHandler;
        private readonly Timer timer = Metric.Timer("Error messages dequeue", Unit.Custom("Messages"));
        //private ProcessErrors processErrors;
        private MessageBodyFactory messageBodyFactory;
        private ErrorIngestionCache errorIngestionCache;
        private ErrorMessageBodyStoragePolicy errorMessageBodyStoragePolicy;
        private RavenBatchOptimizer optimizer;
        private IDocumentStore store;
        private PatchCommandDataFactory patchCommandDataFactory;
        private RecoverabilityBatchCommandFactory recoverabilityBatchCommandFactory;
        private EventLogBatchCommandFactory eventLogBatchCommandFactory;
        private IBus bus;

        public ErrorQueueImport(IBuilder builder, ISendMessages forwarder, IDocumentStore store, IBus bus, CriticalError criticalError, LoggingSettings loggingSettings, Settings settings, IMessageBodyStore messageBodyStore)
        {
            this.store = store;
            this.builder = builder;
            this.forwarder = forwarder;
            this.bus = bus;
            this.criticalError = criticalError;
            this.loggingSettings = loggingSettings;
            this.settings = settings;
            this.messageBodyStore = messageBodyStore;
            messageBodyFactory = new MessageBodyFactory();
            errorIngestionCache = new ErrorIngestionCache(settings);
            errorMessageBodyStoragePolicy = new ErrorMessageBodyStoragePolicy(settings);
            patchCommandDataFactory = new PatchCommandDataFactory(builder.BuildAll<IFailedMessageEnricher>().ToArray(), builder.BuildAll<IEnrichImportedMessages>().Where(x => x.EnrichErrors).ToArray(), errorMessageBodyStoragePolicy, messageBodyStore);
            recoverabilityBatchCommandFactory = new RecoverabilityBatchCommandFactory();

            eventLogBatchCommandFactory = new EventLogBatchCommandFactory();
            //processErrors = new ProcessErrors(store, errorIngestionCache, , bus, criticalError);
        }

        public bool Handle(TransportMessage message)
        {
            using (timer.NewContext())
            {
                InnerHandle(message);

                if (settings.ForwardErrorMessages)
                {
                    TransportMessageCleaner.CleanForForwarding(message);
                    forwarder.Send(message, new SendOptions(settings.ErrorLogQueue));
                }
            }

            return true;
        }

        void InnerHandle(TransportMessage message)
        {
            var uniqueMessageId = message.Headers.UniqueMessageId();
            var failureDetails = ParseFailureDetails(message.Headers);
            var metadata = messageBodyFactory.Create(uniqueMessageId, message);
            var claimCheck = messageBodyStore.Store(BodyStorageTags.ErrorPersistent, message.Body, metadata, errorMessageBodyStoragePolicy);

            var messageFailed = new MessageFailed
            {
                FailedMessageId = uniqueMessageId,
                FailureDetails = failureDetails,
                EndpointId = Address.Parse(failureDetails.AddressOfFailingEndpoint).Queue
            };

            var recoverabilityCommand = recoverabilityBatchCommandFactory.Create(message.Headers, messageFailed);

            var cmds = new ICommandData[2];
            var entities = new object[1];
            if (recoverabilityCommand != null) //Repeated failure
            {
                cmds[0] = recoverabilityCommand;
                cmds[1] = patchCommandDataFactory.Patch(uniqueMessageId, message.Headers, message.Recoverable, claimCheck, failureDetails);
            }
            else // New failure
            {
                entities[0] = patchCommandDataFactory.New(uniqueMessageId, message.Headers, message.Recoverable, claimCheck, failureDetails);
            }

            //entities[1] = eventLogBatchCommandFactory.Create(uniqueMessageId, failureDetails);

            optimizer.Write(entities, cmds);

            bus.Publish(messageFailed);
        }

        class RecoverabilityBatchCommandFactory
        {
            public ICommandData Create(Dictionary<string, string> headers, MessageFailed messageFailed)
            {
                string failedMessageId;
                if (headers.TryGetValue("ServiceControl.Retry.UniqueMessageId", out failedMessageId))
                {
                    messageFailed.FailedMessageId = failedMessageId;

                    return new DeleteCommandData
                    {
                        Key = FailedMessageRetry.MakeDocumentId(failedMessageId)
                    };
                }
                return null;
            }
        }

        class EventLogBatchCommandFactory
        {
            public EventLogItem Create(string failedMessageId, FailureDetails failureDetails)
            {
                var messageFailed = new MessageFailed
                {
                    EndpointId = Address.Parse(failureDetails.AddressOfFailingEndpoint).Queue,
                    FailedMessageId = failedMessageId,
                    FailureDetails = failureDetails
                };

                return new MessageFailedDefinition().Apply(Guid.NewGuid().ToString(), messageFailed);
            }
        }

        FailureDetails ParseFailureDetails(Dictionary<string, string> headers)
        {
            var result = new FailureDetails();

            DictionaryExtensions.CheckIfKeyExists("NServiceBus.TimeOfFailure", headers, s => result.TimeOfFailure = DateTimeExtensions.ToUtcDateTime(s));

            result.Exception = GetException(headers);

            result.AddressOfFailingEndpoint = headers[FaultsHeaderKeys.FailedQ];

            return result;
        }

        ExceptionDetails GetException(IReadOnlyDictionary<string, string> headers)
        {
            var exceptionDetails = new ExceptionDetails();
            DictionaryExtensions.CheckIfKeyExists("NServiceBus.ExceptionInfo.ExceptionType", headers,
                s => exceptionDetails.ExceptionType = s);
            DictionaryExtensions.CheckIfKeyExists("NServiceBus.ExceptionInfo.Message", headers,
                s => exceptionDetails.Message = s);
            DictionaryExtensions.CheckIfKeyExists("NServiceBus.ExceptionInfo.Source", headers,
                s => exceptionDetails.Source = s);
            DictionaryExtensions.CheckIfKeyExists("NServiceBus.ExceptionInfo.StackTrace", headers,
                s => exceptionDetails.StackTrace = s);
            return exceptionDetails;
        }

        public void Start()
        {
            if (!TerminateIfForwardingQueueNotWritable())
            {
                Logger.Info($"Error import is now started, feeding error messages from: {InputAddress}");
            }

            optimizer = new RavenBatchOptimizer(store, CancellationToken.None);
        }

        public void Stop()
        {
            optimizer.Dispose();
        }

        public Address InputAddress => settings.ErrorQueue;

        public bool Disabled => InputAddress == Address.Undefined;

        public Action<TransportReceiver> GetReceiverCustomization()
        {
            satelliteImportFailuresHandler = new SatelliteImportFailuresHandler(builder.Build<IDocumentStore>(),
                Path.Combine(loggingSettings.LogPath, @"FailedImports\Error"), tm => new FailedErrorImport
                {
                    Message = tm,
                }, criticalError);

            return receiver => { receiver.FailureManager = satelliteImportFailuresHandler; };
        }

        bool TerminateIfForwardingQueueNotWritable()
        {
            if (!settings.ForwardErrorMessages)
            {
                return false;
            }

            try
            {
                //Send a message to test the forwarding queue
                var testMessage = new TransportMessage(Guid.Empty.ToString("N"), new Dictionary<string, string>());
                forwarder.Send(testMessage, new SendOptions(settings.ErrorLogQueue));
                return false;
            }
            catch (Exception messageForwardingException)
            {
                criticalError.Raise("Error Import cannot start", messageForwardingException);
                return true;
            }
        }

        public void Dispose()
        {
            satelliteImportFailuresHandler?.Dispose();
        }
    }
}