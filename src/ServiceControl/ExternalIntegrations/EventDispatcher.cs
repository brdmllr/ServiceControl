namespace ServiceControl.ExternalIntegrations
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Netflix.Hystrix;
    using NServiceBus;
    using NServiceBus.CircuitBreakers;
    using NServiceBus.Features;
    using NServiceBus.Logging;
    using Raven.Client;
    using ServiceBus.Management.Infrastructure.Settings;

    public class EventDispatcher : FeatureStartupTask
    {
        public IDocumentStore DocumentStore { get; set; }
        public IBus Bus { get; set; }
        public IEnumerable<IEventPublisher> EventPublishers { get; set; }
        public CriticalError CriticalError { get; set; }

        protected override void OnStart()
        {
            tokenSource = new CancellationTokenSource();
            circuitBreaker = new RepeatedFailuresOverTimeCircuitBreaker("EventDispatcher",
                TimeSpan.FromMinutes(2),
                ex => CriticalError.Raise("Repeated failures when dispatching external integration events.", ex),
                TimeSpan.FromSeconds(20));
            StartDispatcher();
        }

        void StartDispatcher()
        {
            task = Task.Run(() =>
            {
                try
                {
                    DispatchEvents(tokenSource.Token);
                }
                catch (Exception ex)
                {
                    Logger.Error("An exception occurred when dispatching external integration events", ex);
                    circuitBreaker.Failure(ex);

                    if (!tokenSource.IsCancellationRequested)
                    {
                        StartDispatcher();
                    }
                }
            });
        }

        protected override void OnStop()
        {
            tokenSource.Cancel();
            task.Wait();
            tokenSource.Dispose();
        }

        private void DispatchEvents(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (DispatchEventBatch() && !token.IsCancellationRequested)
                {
                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                }
            }
        }

        bool DispatchEventBatch()
        {
            using (var session = DocumentStore.OpenSession())
            {
                var awaitingDispatching = session.Query<ExternalIntegrationDispatchRequest>().Take(Settings.ExternalIntegrationsDispatchingBatchSize).ToList();
                if (!awaitingDispatching.Any())
                {
                    return true;
                }

                var allContexts = awaitingDispatching.Select(r => r.DispatchContext).ToArray();
                if (Logger.IsDebugEnabled)
                {
                    Logger.DebugFormat("Dispatching {0} events.", allContexts.Length);
                }
                var eventsToBePublished = EventPublishers.SelectMany(p => p.PublishEventsForOwnContexts(allContexts, session));

                foreach (var eventToBePublished in eventsToBePublished)
                {
                    if (Logger.IsDebugEnabled)
                    {
                        Logger.DebugFormat("Publishing external event on the bus.");
                    }

                    new Publisher(Bus, eventToBePublished, Logger).Execute();
                }
                foreach (var dispatchedEvent in awaitingDispatching)
                {
                    session.Delete(dispatchedEvent);
                }
                session.SaveChanges();
            }

            return false;
        }

        class Publisher : HystrixCommand<bool>
        {
            private readonly IBus bus;
            private readonly object eventToDispatch;
            private readonly ILog log;

            public Publisher(IBus bus, object eventToDispatch, ILog log) : base(HystrixCommandSetter.WithGroupKey("EventDispatcher")
                .AndCommandKey("Publisher")
                .AndCommandPropertiesDefaults(new HystrixCommandPropertiesSetter()))
            {
                this.bus = bus;
                this.eventToDispatch = eventToDispatch;
                this.log = log;
            }

            protected override bool Run()
            {
                bus.Publish(eventToDispatch);

                return true;
            }

            protected override bool GetFallback()
            {
                log.Error("Failed dispatching external integration event.");

                var publishedEvent = eventToDispatch;
                bus.Publish<ExternalIntegrationEventFailedToBePublished>(m =>
                {
                    m.EventType = publishedEvent.GetType();
                    m.Reason = "Failed dispatching external integration event.";
                });

                return true;
            }
        }

        CancellationTokenSource tokenSource;
        Task task;
        RepeatedFailuresOverTimeCircuitBreaker circuitBreaker;
        static readonly ILog Logger = LogManager.GetLogger(typeof(EventDispatcher));
    }
}