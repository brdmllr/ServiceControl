﻿namespace ServiceBus.Management.Satellites
{
    using System;
    using NServiceBus;
    using NServiceBus.Satellites;
    using Raven.Abstractions.Exceptions;
    using Raven.Client;

    public class AuditMessageImportSatellite : ISatellite
    {
        public IDocumentStore Store { get; set; }

        public bool Handle(TransportMessage message)
        {
            using (var session = Store.OpenSession())
            {
                session.Advanced.UseOptimisticConcurrency = true;

                var processedAt = DateTimeExtensions.ToUtcDateTime(message.Headers[Headers.ProcessingEnded]);

                var auditMessage = new Message(message)
                {
                    Status = MessageStatus.Successful,
                    ProcessedAt = processedAt,
                    Statistics = GetStatistics(message)
                };

                if (message.Headers.ContainsKey("NServiceBus.OriginatingAddress"))
                {
                    auditMessage.ReplyToAddress = message.Headers["NServiceBus.OriginatingAddress"];
                }

                try
                {
                    session.Store(auditMessage);

                    session.SaveChanges();
                }
                catch (ConcurrencyException)
                {
                    UpdateExistingMessage(session, message);
                }
            }

            return true;
        }

        void UpdateExistingMessage(IDocumentSession session, TransportMessage message)
        {
            var processedAt = DateTimeExtensions.ToUtcDateTime(message.Headers[Headers.ProcessingEnded]);

            var auditMessage = session.Load<Message>(message.IdForCorrelation);

            if (auditMessage == null)
                throw new InvalidOperationException("There should be a message in the store");

            if (auditMessage.Status == MessageStatus.Successful && auditMessage.ProcessedAt > processedAt)
            {
                return; //don't overwrite since this message is older
            }

            if (auditMessage.Status != MessageStatus.Successful)
            {
                auditMessage.FailureDetails.ResolvedAt = DateTimeExtensions.ToUtcDateTime(message.Headers[Headers.ProcessingEnded]);
            }

            auditMessage.Status = MessageStatus.Successful;

            if (message.Headers.ContainsKey("NServiceBus.OriginatingAddress"))
            {
                auditMessage.ReplyToAddress = message.Headers["NServiceBus.OriginatingAddress"];
            }

            auditMessage.Statistics = GetStatistics(message);



            session.SaveChanges();
        }

        MessageStatistics GetStatistics(TransportMessage message)
        {
            return new MessageStatistics
                {
                    CriticalTime =
                        DateTimeExtensions.ToUtcDateTime(message.Headers[Headers.ProcessingEnded]) -
                        DateTimeExtensions.ToUtcDateTime(message.Headers[Headers.TimeSent]),
                    ProcessingTime =
                        DateTimeExtensions.ToUtcDateTime(message.Headers[Headers.ProcessingEnded]) -
                        DateTimeExtensions.ToUtcDateTime(message.Headers[Headers.ProcessingStarted])
                };
        }


        public void Start()
        {

        }

        public void Stop()
        {

        }

        public Address InputAddress { get { return Address.Parse("audit"); } }

        public bool Disabled { get { return false; } }
    }
}