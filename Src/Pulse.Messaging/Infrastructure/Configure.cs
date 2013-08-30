﻿namespace Pulse.Messaging.Infrastructure
{
    using System.Globalization;
    using Microsoft.AspNet.SignalR;
    using Microsoft.AspNet.SignalR.Json;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;
    using NServiceBus;

    public class Configure : INeedInitialization
    {
        public void Init()
        {
            var serializer = new JsonNetSerializer(new JsonSerializerSettings
            {
                ContractResolver = new CustomSignalRContractResolverBecauseOfIssue500InSignalR(),
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore,
                Converters = { new IsoDateTimeConverter { DateTimeStyles = DateTimeStyles.RoundtripKind } }
            });

            GlobalHost.DependencyResolver.Register(typeof(IJsonSerializer), () => serializer); 
        }
    }
}