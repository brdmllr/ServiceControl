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

    public class ErrorQueueImport : IAdvancedSatellite, IDisposable
    {
        public ISendMessages Forwarder { get; set; }
        public IBuilder Builder { get; set; }
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
                .AndCommandKey("ErrorQueue")
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
                var errorMessageReceived = new ImportFailedMessage(message);

                using (var childBuilder = builder.CreateChildBuilder())
                {
                    pipelineExecutor.CurrentContext.Set(childBuilder);

                    foreach (var enricher in childBuilder.BuildAll<IEnrichImportedMessages>())
                    {
                        enricher.Enrich(errorMessageReceived);
                    }

                    var logicalMessage = logicalMessageFactory.Create(errorMessageReceived);

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
                if (Settings.ForwardErrorMessages)
                {
                    TransportMessageCleaner.CleanForForwarding(message);
                    forwarder.Send(message, new SendOptions(Settings.ErrorLogQueue));
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
            if (!TerminateIfForwardingQueueNotWritable())
            {
                Logger.InfoFormat("Error import is now started, feeding error messages from: {0}", InputAddress);
            }
        }

        public void Stop()
        {
        }

        public Address InputAddress => Settings.ErrorQueue;

        public bool Disabled => InputAddress == Address.Undefined;

        public Action<TransportReceiver> GetReceiverCustomization()
        {
            satelliteImportFailuresHandler = new SatelliteImportFailuresHandler(Builder.Build<IDocumentStore>(),
                Path.Combine(LoggingSettings.LogPath, @"FailedImports\Error"), tm => new FailedErrorImport
                {
                    Message = tm,
                }, CriticalError);

            return receiver => { receiver.FailureManager = satelliteImportFailuresHandler; };
        }

        bool TerminateIfForwardingQueueNotWritable()
        {
            if (!Settings.ForwardErrorMessages)
            {
                return false;
            }

            try
            {
                //Send a message to test the forwarding queue
                var testMessage = new TransportMessage(Guid.Empty.ToString("N"), new Dictionary<string, string>());
                Forwarder.Send(testMessage, new SendOptions(Settings.ErrorLogQueue));
                return false;
            }
            catch (Exception messageForwardingException)
            {
                //This call to RaiseCriticalError has to be on a seperate thread  otherwise it deadlocks and doesn't stop correctly.  
                ThreadPool.QueueUserWorkItem(state => CriticalError.Raise("Error Import cannot start", messageForwardingException));
                return true;
            }
        }

        public void Dispose()
        {
            satelliteImportFailuresHandler?.Dispose();
        }

        SatelliteImportFailuresHandler satelliteImportFailuresHandler;

        static readonly ILog Logger = LogManager.GetLogger(typeof(ErrorQueueImport));
    }
}