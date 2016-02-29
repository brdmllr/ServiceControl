namespace Hystrix.MetricsEventStream
{
    using System;
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Threading;
    using slf4net;
    using ServiceControl.Infrastructure;

    class HystrixMetricsStreamer
    {
        private static readonly TimeSpan DefaultSendInterval = TimeSpan.FromSeconds(1);
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(HystrixMetricsStreamer));
        static readonly TimeSpan SpinFor = TimeSpan.FromSeconds(1);

        private readonly TimeKeeper timeKeeper;

        private HttpListenerContext context;
        private readonly Action<HystrixMetricsStreamer> disconnected;
        private ConcurrentQueue<string> metricsDataQueue = new ConcurrentQueue<string>();
        private HystrixMetricsSampler sampler;
        private bool stopping;
        private Timer timer;

        public HystrixMetricsStreamer(HystrixMetricsSampler sampler, TimeKeeper timeKeeper, HttpListenerContext context, Action<HystrixMetricsStreamer> disconnected)
        {
            this.sampler = sampler;
            this.timeKeeper = timeKeeper;
            this.context = context;
            this.disconnected = disconnected;
        }

        public void Start()
        {
            sampler.SampleDataAvailable += Sampler_SampleDataAvailable;

            timer = timeKeeper.New(DoWork, TimeSpan.Zero, GetSendInterval());
        }

        public void Stop()
        {
            stopping = true;
            sampler.SampleDataAvailable -= Sampler_SampleDataAvailable;

            timeKeeper.Release(timer);
        }

        /// <inheritdoc />
        void DoWork()
        {
            try
            {
                context.Response.AppendHeader("Content-Type", "text/event-stream;charset=UTF-8");
                context.Response.AppendHeader("Cache-Control", "no-cache, no-store, max-age=0, must-revalidate");
                context.Response.AppendHeader("Pragma", "no-cache");
                //context.Response.AppendHeader("Access-Control-Expose-Headers", "ETag, Last-Modified, Link, Total-Count, X-Particular-Version");
                //context.Response.AppendHeader("Access-Control-Allow-Headers", "Origin, X-Requested-With, Content-Type, Accept");
                context.Response.AppendHeader("Access-Control-Allow-Methods", "GET");
                context.Response.AppendHeader("Access-Control-Allow-Origin", "*");

                using (var outputWriter = new StreamWriter(context.Response.OutputStream))
                {
                    while (!stopping)
                    {
                        string data;
                        if (metricsDataQueue.TryDequeue(out data))
                        {
                            outputWriter.WriteLine("data: {0}\n", data);

                            outputWriter.Flush();

                            continue;
                        }

                        SpinWait.SpinUntil(() => stopping, SpinFor);
                    }
                }

                context.Response.Close();
            }
            catch (HttpListenerException ex)
            {
                Logger.Info(ex, "Streaming connection closed by client.");
                disconnected(this);
            }
            
        }

        /// <summary>
        ///     Extracts the sending time interval from the HTTP request.
        /// </summary>
        /// <returns>The time interval between sending new metrics data.</returns>
        private TimeSpan GetSendInterval()
        {
            TimeSpan sendInterval = DefaultSendInterval;
            if (context.Request.QueryString["delay"] != null)
            {
                int streamDelayInMilliseconds;
                if (int.TryParse(context.Request.QueryString["delay"], out streamDelayInMilliseconds))
                {
                    sendInterval = TimeSpan.FromMilliseconds(streamDelayInMilliseconds);
                }
                else
                {
                    Logger.Warn(CultureInfo.InvariantCulture, "Invalid delay parameter in request: '{0}'", streamDelayInMilliseconds);
                }
            }

            return sendInterval;
        }

        private void Sampler_SampleDataAvailable(object sender, SampleDataAvailableEventArgs e)
        {
            foreach (var data in e.Data)
            {
                metricsDataQueue.Enqueue(data);
            }
        }
    }
}