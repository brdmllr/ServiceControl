namespace ServiceControl.EndpointControl.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Infrastructure;
    using InternalMessages;
    using NServiceBus;
    using NServiceBus.Features;
    using Operations;
    using ServiceControl.Contracts.Operations;

    public class EndpointDetectionFeature : Feature
    {
        public EndpointDetectionFeature()
        {
            EnableByDefault();
        }

        protected override void Setup(FeatureConfigurationContext context)
        {
            context.Container.ConfigureComponent<EnrichWithEndpointDetails>(DependencyLifecycle.SingleInstance);
            context.Container.ConfigureComponent<DetectNewEndpointsImportBatchMonitor>(DependencyLifecycle.SingleInstance);
        }

        class EnrichWithEndpointDetails : ImportEnricher
        {
            public override void Enrich(IReadOnlyDictionary<string, string> headers, IDictionary<string, object> metadata)
            {
                var sendingEndpoint = EndpointDetailsParser.SendingEndpoint(headers);
                // SendingEndpoint will be null for messages that are from v3.3.x endpoints because we don't
                // have the relevant information via the headers, which were added in v4.
                if (sendingEndpoint != null)
                {
                    metadata.Add("SendingEndpoint", sendingEndpoint);
                }

                var receivingEndpoint = EndpointDetailsParser.ReceivingEndpoint(headers);
                // The ReceivingEndpoint will be null for messages from v3.3.x endpoints that were successfully
                // processed because we dont have the information from the relevant headers.
                if (receivingEndpoint != null)
                {
                    metadata.Add("ReceivingEndpoint", receivingEndpoint);
                }
            }
        }

        class Pre45EndpointStrategy : IEndpointDetectionStrategy, IEqualityComparer<EndpointDetails>
        {
            public IEnumerable<EndpointDetails> FindUniqueMatchingEndpoints(IEnumerable<EndpointDetails> input)
                => input.Where(x => x.HostId == Guid.Empty).Distinct(this);

            public Guid MakeEndpointId(EndpointDetails endpointDetails)
                // since for pre 4.5 endpoints we wont have a hostid then fake one
                => DeterministicGuid.MakeId(endpointDetails.Name, endpointDetails.Host);

            public RegisterEndpoint CreateRegisterMessage(Guid endpointInstanceId, EndpointDetails endpoint)
                => new RegisterEndpoint
                    {
                        //we don't send endpoint instance id since we don't have the host id
                        Endpoint = endpoint,
                        DetectedAt = DateTime.UtcNow
                    };


            public bool Equals(EndpointDetails x, EndpointDetails y) => x.Name == y.Name && x.Host == y.Host;

            // NOTE: From http://stackoverflow.com/a/1646913/358
            public int GetHashCode(EndpointDetails obj)
            {
                var hash = 17;
                hash = hash * 31 + obj.Name.GetHashCode();
                hash = hash * 31 + obj.Host.GetHashCode();
                return hash;
            }
        }

        class Post45EndpointStrategy : IEndpointDetectionStrategy, IEqualityComparer<EndpointDetails>
        {
            public IEnumerable<EndpointDetails> FindUniqueMatchingEndpoints(IEnumerable<EndpointDetails> input)
                => input.Where(x => x.HostId != Guid.Empty).Distinct(this);

            public Guid MakeEndpointId(EndpointDetails endpointDetails)
                => DeterministicGuid.MakeId(endpointDetails.Name, endpointDetails.HostId.ToString());

            public RegisterEndpoint CreateRegisterMessage(Guid endpointInstanceId, EndpointDetails endpoint)
                => new RegisterEndpoint
                    {
                        EndpointInstanceId = endpointInstanceId,
                        Endpoint = endpoint,
                        DetectedAt = DateTime.UtcNow
                    };

            public bool Equals(EndpointDetails x, EndpointDetails y) => x.Name == y.Name && x.HostId == y.HostId;

            // NOTE: From http://stackoverflow.com/a/1646913/358
            public int GetHashCode(EndpointDetails obj)
            {
                var hash = 17;
                hash = hash * 31 + obj.Name.GetHashCode();
                hash = hash * 31 + obj.HostId.GetHashCode();
                return hash;
            }
        }

        interface IEndpointDetectionStrategy
        {
            IEnumerable<EndpointDetails> FindUniqueMatchingEndpoints(IEnumerable<EndpointDetails> input);
            Guid MakeEndpointId(EndpointDetails endpointDetails);
            RegisterEndpoint CreateRegisterMessage(Guid endpointInstanceId, EndpointDetails endpoint);
        }

        class DetectNewEndpointsImportBatchMonitor : IMonitorImportBatches
        {
            public DetectNewEndpointsImportBatchMonitor(IBus bus, KnownEndpointsCache knownEndpointsCache)
            {
                this.bus = bus;
                this.knownEndpointsCache = knownEndpointsCache;
            }

            public void Accept(IEnumerable<Dictionary<string, object>> batch)
            {
                var endpoints = ExtractEndpoints(batch).ToArray();

                foreach (var message in DetectNewEndpoints(endpoints, post45EndpointStrategy))
                {
                    bus.SendLocal(message);
                }

                foreach (var message in DetectNewEndpoints(endpoints, pre45EndpointStrategy))
                {
                    bus.SendLocal(message);
                }
            }

            private IEnumerable<EndpointDetails> ExtractEndpoints(IEnumerable<IDictionary<string, object>> batch)
            {
                foreach (var messageMetadata in batch)
                {
                    object endpointDetails;
                    if (messageMetadata.TryGetValue("SendingEndpoint", out endpointDetails))
                    {
                        yield return (EndpointDetails)endpointDetails;
                    }
                    if (messageMetadata.TryGetValue("ReceivingEndpoint", out endpointDetails))
                    {
                        yield return (EndpointDetails)endpointDetails;
                    }
                }
            }

            private IEnumerable<RegisterEndpoint> DetectNewEndpoints(EndpointDetails[] endpoints, IEndpointDetectionStrategy strategy)
                => from endpoint in strategy.FindUniqueMatchingEndpoints(endpoints)
                   let endpointInstanceId = strategy.MakeEndpointId(endpoint)
                   where knownEndpointsCache.TryAdd(endpointInstanceId)
                   select strategy.CreateRegisterMessage(endpointInstanceId, endpoint);

            private IBus bus;
            private KnownEndpointsCache knownEndpointsCache;
            private Pre45EndpointStrategy pre45EndpointStrategy = new Pre45EndpointStrategy();
            private Post45EndpointStrategy post45EndpointStrategy = new Post45EndpointStrategy();
        }
    }
}