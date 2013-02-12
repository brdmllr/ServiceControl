﻿namespace ServiceBus.Management.AuditMessages
{
    using System;
    using NServiceBus;
    using NServiceBus.Satellites;
    using Raven.Client;

    public class AuditMessageImportSatellite : ISatellite
    {
        public IDocumentStore Store { get; set; }

        public bool Handle(TransportMessage message)
        {
            using (var session = Store.OpenSession())
            {
               
                var auditMessage = session.Load<Message>(message.IdForCorrelation);

                if (auditMessage == null)
                {
                    auditMessage = new Message(message)
                        {
                            Status = MessageStatus.Successfull
                        };
                }
                else
                {
                    if (auditMessage.Status == MessageStatus.Successfull)
                        throw new InvalidOperationException("Duplicate audit message detected " + message.IdForCorrelation);

                    auditMessage.FailureDetails.ResolvedAt =
                        DateTimeExtensions.ToUtcDateTime(message.Headers[Headers.ProcessingEnded]);

                    auditMessage.Status = MessageStatus.Successfull;
                }

                if (message.Headers.ContainsKey("NServiceBus.OriginatingAddress"))
                    auditMessage.ReplyToAddress = message.Headers["NServiceBus.OriginatingAddress"];

                auditMessage.Statistics = GetStatistics(message);

                session.Store(auditMessage);

                session.SaveChanges();
            }

            return true;
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