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

        private ConcurrentDictionary<HystrixMetricsStreamer, object> streamers = new ConcurrentDictionary<HystrixMetricsStreamer, object>();
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

            foreach (var keyValuePair in streamers)
            {
                keyValuePair.Key.Stop();
            }

            messagePumpTask.Wait();
        }

        async Task ProcessMessages()
        {
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(false);

                    if (cancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }

                    CreateAndStartStreamer(context);
                }
                catch (HttpListenerException ex)
                {
                    // a HttpListenerException can occur on listener.GetContext when we shutdown. this can be ignored
                    if (!cancellationTokenSource.IsCancellationRequested)
                    {
                        Logger.Error("Stream server failed to receive incoming request.", ex);
                    }
                    break;
                }
                catch (ObjectDisposedException ex)
                {
                    // a ObjectDisposedException can occur on listener.GetContext when we shutdown. this can be ignored
                    if (!cancellationTokenSource.IsCancellationRequested && ex.ObjectName == typeof(HttpListener).FullName)
                    {
                        Logger.Error("Stream server failed to receive incoming request.", ex);
                    }
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    Logger.Error("Stream server failed to receive incoming request.", ex);
                    break;
                }
            }
        }

        private void CreateAndStartStreamer(HttpListenerContext context)
        {
            var streamer = new HystrixMetricsStreamer(sampler, timeKeeper, context, s =>
            {
                object _;
                streamers.TryRemove(s, out _);
                s.Stop();
            });

            streamers.TryAdd(streamer, null);
            streamer.Start();
        }
    }
}