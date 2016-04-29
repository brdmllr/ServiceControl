namespace ServiceControl.Operations
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Contracts.Operations;
    using Netflix.Hystrix;
    using NServiceBus;
    using NServiceBus.Logging;
    using NServiceBus.ObjectBuilder;
    using NServiceBus.Pipeline;
    using NServiceBus.Pipeline.Contexts;
    using NServiceBus.Satellites;
    using NServiceBus.Transports;
    using NServiceBus.Unicast;
    using NServiceBus.Unicast.Messages;
    using NServiceBus.Unicast.Transport;
    using Raven.Client;
    using ServiceBus.Management.Infrastructure.Settings;
    using ServiceControl.Infrastructure.RavenDB;

    public class AuditQueueImport : IAdvancedSatellite, IDisposable
    {
        public IBuilder Builder { get; set; }
        public ISendMessages Forwarder { get; set; }
        public PipelineExecutor PipelineExecutor { get; set; }
        public LogicalMessageFactory LogicalMessageFactory { get; set; }
        public CriticalError CriticalError { get; set; }

        class Importer : HystrixCommand<bool>
        {
            private readonly TransportMessage message;
            private readonly IBuilder builder;
            private readonly PipelineExecutor pipelineExecutor;
            private readonly LogicalMessageFactory logicalMessageFactory;
            private readonly ISendMessages forwarder;

            public Importer(TransportMessage message, IBuilder builder, PipelineExecutor pipelineExecutor, LogicalMessageFactory logicalMessageFactory, ISendMessages forwarder) : base(HystrixCommandSetter.WithGroupKey("Importer")
                .AndCommandKey("AuditQueue")
                .AndCommandPropertiesDefaults(new HystrixCommandPropertiesSetter().WithCircuitBreakerEnabled(false).WithExecutionIsolationStrategy(ExecutionIsolationStrategy.Semaphore).WithExecutionIsolationSemaphoreMaxConcurrentRequests(1000)))
            {
                this.message = message;
                this.builder = builder;
                this.pipelineExecutor = pipelineExecutor;
                this.logicalMessageFactory = logicalMessageFactory;
                this.forwarder = forwarder;
            }

            protected override bool Run()
            {
                var receivedMessage = new ImportSuccessfullyProcessedMessage(message);

                using (var childBuilder = builder.CreateChildBuilder())
                {
                    pipelineExecutor.CurrentContext.Set(childBuilder);

                    foreach (var enricher in childBuilder.BuildAll<IEnrichImportedMessages>())
                    {
                        enricher.Enrich(receivedMessage);
                    }

                    var logicalMessage = logicalMessageFactory.Create(receivedMessage);

                    var context = new IncomingContext(pipelineExecutor.CurrentContext, message)
                    {
                        LogicalMessages = new List<LogicalMessage>
                    {
                        logicalMessage
                    },
                        IncomingLogicalMessage = logicalMessage
                    };

                    context.Set("NServiceBus.CallbackInvocationBehavior.CallbackWasInvoked", false);

                    var behaviors = behavioursToAddFirst.Concat(pipelineExecutor.Incoming.SkipWhile(r => r.StepId != WellKnownStep.LoadHandlers).Select(r => r.BehaviorType));

                    pipelineExecutor.InvokePipeline(behaviors, context);
                }

                if (Settings.ForwardAuditMessages)
                {
                    TransportMessageCleaner.CleanForForwarding(message);
                    forwarder.Send(message, new SendOptions(Settings.AuditLogQueue));
                }

                return true;
            }

            Type[] behavioursToAddFirst = { typeof(RavenUnitOfWorkBehavior) };
        }

        public bool Handle(TransportMessage message)
        {
            new Importer(message, Builder, PipelineExecutor, LogicalMessageFactory, Forwarder).Execute();

            return true;
        }

        public void Start()
        {
            if (!TerminateIfForwardingIsEnabledButQueueNotWritable())
            {
                Logger.InfoFormat("Audit import is now started, feeding audit messages from: {0}", InputAddress);
            }
        }

        bool TerminateIfForwardingIsEnabledButQueueNotWritable()
        {
            if (!Settings.ForwardAuditMessages)
            {
                return false;
            }

            try
            {
                //Send a message to test the forwarding queue
                var testMessage = new TransportMessage(Guid.Empty.ToString("N"), new Dictionary<string, string>());
                Forwarder.Send(testMessage, new SendOptions(Settings.AuditLogQueue));
                return false;
            }
            catch (Exception messageForwardingException)
            {
                //This call to RaiseCriticalError has to be on a seperate thread  otherwise it deadlocks and doesn't stop correctly.  
                ThreadPool.QueueUserWorkItem(state => CriticalError.Raise("Audit Import cannot start", messageForwardingException));
                return true;
            }
        }


        public void Stop()
        {
        }

        public Address InputAddress => Settings.AuditQueue;

        public bool Disabled => false;

        public Action<TransportReceiver> GetReceiverCustomization()
        {
            satelliteImportFailuresHandler = new SatelliteImportFailuresHandler(Builder.Build<IDocumentStore>(),
                Path.Combine(LoggingSettings.LogPath, @"FailedImports\Audit"), tm => new FailedAuditImport
                {
                    Message = tm,
                },
                CriticalError);

            return receiver => { receiver.FailureManager = satelliteImportFailuresHandler; };
        }

        public void Dispose()
        {
            satelliteImportFailuresHandler?.Dispose();
        }

        SatelliteImportFailuresHandler satelliteImportFailuresHandler;

        static readonly ILog Logger = LogManager.GetLogger(typeof(AuditQueueImport));
    }
}