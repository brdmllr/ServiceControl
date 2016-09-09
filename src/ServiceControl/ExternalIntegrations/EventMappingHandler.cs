﻿namespace ServiceControl.ExternalIntegrations
{
    using System.Collections.Generic;
    using System.Linq;
    using NServiceBus;
    using NServiceBus.Logging;
    using Raven.Client;

    public class EventMappingHandler : IHandleMessages<IEvent>
    {
        private readonly IDocumentStore store;
        private readonly IEnumerable<IEventPublisher> eventPublishers;

        public EventMappingHandler(IDocumentStore store, IEnumerable<IEventPublisher> eventPublishers)
        {
            this.store = store;
            this.eventPublishers = eventPublishers;
        }
        public void Handle(IEvent message)
        {
            var dispatchContexts = eventPublishers
                .Where(p => p.Handles(message))
                .Select(p => p.CreateDispatchContext(message));

            foreach (var dispatchContext in dispatchContexts)
            {
                if (Logger.IsDebugEnabled)
                {
                    Logger.Debug("Storing dispatch request.");
                }
                var dispatchRequest = new ExternalIntegrationDispatchRequest
                {
                    DispatchContext = dispatchContext
                };

                using (var session = store.OpenSession())
                {
                    session.Store(dispatchRequest);
                    session.SaveChanges();
                }
            }
        }

        static readonly ILog Logger = LogManager.GetLogger(typeof(EventMappingHandler));
    }
}