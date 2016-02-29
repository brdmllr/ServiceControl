namespace Hystrix.MetricsEventStream
{
    using NServiceBus;
    using NServiceBus.Features;
    using ServiceControl.Infrastructure;

    class Hystrix: Feature
    {
        public Hystrix()
        {
            EnableByDefault();

            RegisterStartupTask<HystrixMetricsStreamServer>();
        }

        protected override void Setup(FeatureConfigurationContext context)
        {
            context.Container.ConfigureComponent(builder => new HystrixMetricsStreamServer(builder.Build<TimeKeeper>(), "http://localhost:8087/Hystrix"), DependencyLifecycle.SingleInstance);

        }
    }
}