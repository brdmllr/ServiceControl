namespace Hystrix.MetricsEventStream
{
    using System;
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Owin;
    using slf4net;
    using ServiceControl.Infrastructure;

    class HystrixStreamerMiddleware : OwinMiddleware
    {
        private readonly HystrixMetricsSampler sampler;
        private readonly TimeKeeper timeKeeper;
        static readonly TimeSpan SpinFor = TimeSpan.FromSeconds(1);
        private ConcurrentQueue<string> metricsDataQueue = new ConcurrentQueue<string>();

        public HystrixStreamerMiddleware(OwinMiddleware next, TimeKeeper timeKeeper) : base(next)
        {
            this.timeKeeper = timeKeeper;
            sampler = new HystrixMetricsSampler(timeKeeper);
            sampler.Start();
        }

        public override Task Invoke(IOwinContext context)
        {
            sampler.SampleDataAvailable += Sampler_SampleDataAvailable;

            var owinCallCancelled = context.Get<CancellationToken>("owin.CallCancelled");
            var tcs = new TaskCompletionSource<int>();
            var timer = timeKeeper.New(() => SendStats(context, owinCallCancelled, tcs), TimeSpan.Zero, GetSendInterval(context));

            owinCallCancelled.Register(() =>
            {
                sampler.SampleDataAvailable -= Sampler_SampleDataAvailable;

                timeKeeper.Release(timer);

                tcs.SetResult(1);
            });

            return tcs.Task;
        }

        private void SendStats(IOwinContext context, CancellationToken owinCallCancelled, TaskCompletionSource<int> tcs)
        {
            context.Response.Headers["Content-Type"] = "text/event-stream;charset=UTF-8";
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, max-age=0, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET";
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";

            using (var outputWriter = new StreamWriter(context.Response.Body))
            {
                while (!owinCallCancelled.IsCancellationRequested)
                {
                    string data;
                    if (metricsDataQueue.TryDequeue(out data))
                    {
                        outputWriter.WriteLine("data: {0}\n", data);

                        outputWriter.Flush();

                        continue;
                    }

                    SpinWait.SpinUntil(() => owinCallCancelled.IsCancellationRequested, SpinFor);
                }
            }

            tcs.SetResult(1);
        }

        private TimeSpan GetSendInterval(IOwinContext context)
        {
            var sendInterval = DefaultSendInterval;

            if (context.Request.Query["delay"] != null)
            {
                int streamDelayInMilliseconds;
                if (int.TryParse(context.Request.Query["delay"], out streamDelayInMilliseconds))
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

        private static readonly TimeSpan DefaultSendInterval = TimeSpan.FromSeconds(1);

        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(HystrixStreamerMiddleware));

        private void Sampler_SampleDataAvailable(object sender, SampleDataAvailableEventArgs e)
        {
            foreach (var data in e.Data)
            {
                metricsDataQueue.Enqueue(data);
            }
        }
    }
}