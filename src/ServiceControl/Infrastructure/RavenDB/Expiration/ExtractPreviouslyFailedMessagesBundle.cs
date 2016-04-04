namespace ServiceControl.Infrastructure.RavenDB.Expiration
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using NServiceBus;
    using NServiceBus.Logging;
    using NServiceBus.Transports;
    using NServiceBus.Unicast;
    using Raven.Abstractions;
    using Raven.Abstractions.Data;
    using Raven.Abstractions.Extensions;
    using Raven.Client;

    public class ExtractPreviouslyFailedMessagesBundle : IWantToRunWhenBusStartsAndStops
    {
        public ISendMessages SendMessages { get; set; }

        public IDocumentStore Store { get; set; }

        public void Start()
        {
            var failures = Store.DatabaseCommands.Query("FailedErrorImportIndex", new IndexQuery
            {
                Start = 0,
                PageSize = 1000,
                Cutoff = SystemTime.UtcNow,
                DisableCaching = true,
                FieldsToFetch = new[]
                {
                    "__document_id",
                    "Message"
                }
            }, new string[0]);

            logger.InfoFormat("Dealing with previously failed messages, extracting {0}/{1} and moving them to error2.", failures.Results.Count, failures.TotalResults);

            Parallel.ForEach(failures.Results, doc =>
            {
                var message = doc["Message"].JsonDeserialization<TransportMessageRaw>();
                SendMessages.Send(new TransportMessage(message.Id, message.Headers)
                {
                    Body = message.Body,
                    Recoverable = message.Recoverable,
                    TimeToBeReceived = message.TimeToBeReceived
                }, new SendOptions("error2"));

                var id = doc.Value<string>("__document_id");
                Store.DatabaseCommands.Delete(id, null);
            });

            logger.InfoFormat("Dealing with previously failed messages, extracting {0}/{1} and moving them to error2 completed.", failures.Results.Count, failures.TotalResults);

        }

        public void Stop()
        {
        }

        static ILog logger = LogManager.GetLogger<ExtractPreviouslyFailedMessagesBundle>();

        public class TransportMessageRaw
        {
            /// <summary>
            /// Gets/sets the identifier of this message bundle.
            /// 
            /// </summary>
            public string Id { get; set; }

            /// <summary>
            /// Gets/sets the unique identifier of another message bundle
            ///                 this message bundle is associated with.
            /// 
            /// </summary>
            public string CorrelationId { get; set; }

            /// <summary>
            /// Gets/sets the reply-to address of the message bundle - replaces 'ReturnAddress'.
            /// 
            /// </summary>
            public Address ReplyToAddress { get; set; }

            /// <summary>
            /// Gets/sets whether or not the message is supposed to
            ///                 be guaranteed deliverable.
            /// 
            /// </summary>
            public bool Recoverable { get; set; }

            /// <summary>
            /// Indicates to the infrastructure the message intent (publish, or regular send).
            /// 
            /// </summary>
            public MessageIntentEnum MessageIntent { get; set; }

            /// <summary>
            /// Gets/sets the maximum time limit in which the message bundle
            ///                 must be received.
            /// 
            /// </summary>
            public TimeSpan TimeToBeReceived { get; set; }

            /// <summary>
            /// Gets/sets other applicative out-of-band information.
            /// 
            /// </summary>
            public Dictionary<string, string> Headers { get; set; }

            /// <summary>
            /// Gets/sets a byte array to the body content of the message
            /// 
            /// </summary>
            public byte[] Body { get; set; }
        }
    }
}