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

            public bool Matches(EndpointDetails endpoint) => endpoint.HostId == Guid.Empty;

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
            public Guid MakeEndpointId(EndpointDetails endpointDetails)
                => DeterministicGuid.MakeId(endpointDetails.Name, endpointDetails.HostId.ToString());

            public RegisterEndpoint CreateRegisterMessage(Guid endpointInstanceId, EndpointDetails endpoint)
                => new RegisterEndpoint
                    {
                        EndpointInstanceId = endpointInstanceId,
                        Endpoint = endpoint,
                        DetectedAt = DateTime.UtcNow
                    };

            public bool Matches(EndpointDetails endpoint) => endpoint.HostId != Guid.Empty;

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
            Guid MakeEndpointId(EndpointDetails endpointDetails);
            RegisterEndpoint CreateRegisterMessage(Guid endpointInstanceId, EndpointDetails endpoint);
            bool Matches(EndpointDetails endpoint);
        }

        class DetectNewEndpointsImportBatchMonitor : IMonitorImportBatches
        {
            public DetectNewEndpointsImportBatchMonitor(IBus bus, KnownEndpointsCache knownEndpointsCache)
            {
                this.bus = bus;
                this.knownEndpointsCache = knownEndpointsCache;

                pre45EndpointStrategy = new Pre45EndpointStrategy();
                pre45Endpoints = new HashSet<EndpointDetails>(pre45EndpointStrategy);

                post45EndpointStrategy = new Post45EndpointStrategy();
                post45Endpoints = new HashSet<EndpointDetails>(post45EndpointStrategy);
            }

            public void Accept(Dictionary<string, object> metadata)
            {
                Add(metadata, "SendingEndpoint");
                Add(metadata, "ReceivingEndpoint");
            }

            private void Add(Dictionary<string, object> metadata, string key)
            {
                object value;
                if (metadata.TryGetValue(key, out value))
                {
                    var endpoint = (EndpointDetails)value;
                    if (post45EndpointStrategy.Matches(endpoint))
                    {
                        post45Endpoints.Add(endpoint);
                    }
                    else if (pre45EndpointStrategy.Matches(endpoint))
                    {
                        pre45Endpoints.Add(endpoint);
                    }
                }
            }

            public void FinalizeBatch()
            {
                foreach (var message in CreateMessages(pre45Endpoints, pre45EndpointStrategy))
                {
                    bus.SendLocal(message);
                }

                foreach (var message in CreateMessages(post45Endpoints, post45EndpointStrategy))
                {
                    bus.SendLocal(message);
                }

                post45Endpoints.Clear();
                pre45Endpoints.Clear();
            }

            private IEnumerable<RegisterEndpoint> CreateMessages(HashSet<EndpointDetails> endpoints, IEndpointDetectionStrategy strategy)
                => from endpoint in endpoints
                   let endpointInstanceId = strategy.MakeEndpointId(endpoint)
                   where knownEndpointsCache.TryAdd(endpointInstanceId)
                   select strategy.CreateRegisterMessage(endpointInstanceId, endpoint);

            private IBus bus;
            private KnownEndpointsCache knownEndpointsCache;

            private Pre45EndpointStrategy pre45EndpointStrategy;
            private Post45EndpointStrategy post45EndpointStrategy;

            private HashSet<EndpointDetails> pre45Endpoints;
            private HashSet<EndpointDetails> post45Endpoints;
        }
    }
}