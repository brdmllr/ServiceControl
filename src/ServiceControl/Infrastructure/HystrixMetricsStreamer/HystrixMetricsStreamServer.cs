namespace Hystrix.MetricsEventStream
{
    using System;
    using System.Collections.Concurrent;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using NServiceBus.Features;
    using NServiceBus.Logging;
    using ServiceControl.Infrastructure;

    class HystrixMetricsStreamServer : FeatureStartupTask
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(HystrixMetricsStreamServer));
        private readonly TimeKeeper timeKeeper;
        private CancellationTokenSource cancellationTokenSource;
        private string httpListenerPrefix;

        private HttpListener listener;

        private HystrixMetricsSampler sampler;

        private ConcurrentBag<HystrixMetricsStreamer> streamers = new ConcurrentBag<HystrixMetricsStreamer>();
        private Task messagePumpTask;

        public HystrixMetricsStreamServer(TimeKeeper timeKeeper, string httpListenerPrefix)
        {
            this.timeKeeper = timeKeeper;
            this.httpListenerPrefix = httpListenerPrefix;
        }

        protected override void OnStart()
        {
            try
            {
                Logger.InfoFormat("Starting metrics stream server on '{0}'.", httpListenerPrefix);
                listener = new HttpListener();
                listener.Prefixes.Add(httpListenerPrefix);
                listener.Start();
            }
            catch (Exception ex)
            {
                listener?.Stop();

                Logger.Error("Failed to start stream server.", ex);

                throw;
            }
            cancellationTokenSource = new CancellationTokenSource();

            sampler = new HystrixMetricsSampler(timeKeeper);
            sampler.Start();

            messagePumpTask = Task.Run(ProcessMessages, CancellationToken.None);
        }

        protected override void OnStop()
        {
            cancellationTokenSource?.Cancel();
            listener?.Close();
            sampler?.Stop();

            foreach (var s in streamers)
            {
                s.Stop();
            }

            messagePumpTask.Wait();
        }

        async Task ProcessMessages()
        {
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);

                if (cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                CreateAndStartStreamer(context);
            }
        }

        private void CreateAndStartStreamer(HttpListenerContext context)
        {
            var streamer = new HystrixMetricsStreamer(sampler, timeKeeper, context);
            streamers.Add(streamer);
            streamer.Start();
        }
    }
}