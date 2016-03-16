namespace ServiceControl.MessageFailures.Api
{
    using System;
    using System.Linq;
    using Contracts.Operations;
    using MessageFailures;
    using Raven.Client.Indexes;

    public class FailedMessageViewIndex : AbstractIndexCreationTask<FailedMessage>
    {
        public class SortAndFilterOptions: IHaveStatus
        {
            public string MessageId { get; set; }
            public DateTime TimeSent { get; set; }
            public string MessageType { get; set; }
            public FailedMessageStatus Status { get; set; }
            public string ReceivingEndpointName { get; set; }
            public long LastModified { get; set; }

        }

        public FailedMessageViewIndex()
        {
            Map = messages => from message in messages
                              let metadata = message.ProcessingAttempts.Last().MessageMetadata
           select new
            {
                MessageId = metadata["MessageId"],
                MessageType = metadata["MessageType"], 
                message.Status,
                TimeSent = (DateTime)metadata["TimeSent"],
                ReceivingEndpointName = ((EndpointDetails)metadata["ReceivingEndpoint"]).Name,
                LastModified = MetadataFor(message).Value<DateTime>("Last-Modified").Ticks
           };

            DisableInMemoryIndexing = true;
        }
    }
}