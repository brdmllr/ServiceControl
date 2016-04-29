namespace Hystrix.MetricsEventStream
{
    using System;
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.IO;
    using System.Reactive.Concurrency;
    using System.Reactive.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Owin;
    using slf4net;
    using ServiceControl.Infrastructure;

    class HystrixStreamerMiddleware : OwinMiddleware
    {
        private readonly OwinMiddleware next;
        private static readonly TimeSpan DefaultSendInterval = TimeSpan.FromSeconds(1);
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(HystrixStreamerMiddleware));
        private readonly HystrixMetricsSampler sampler;
        private ConcurrentQueue<string> metricsDataQueue = new ConcurrentQueue<string>();
        private CancellationTokenSource shutdownTokenSource = new CancellationTokenSource();

        public HystrixStreamerMiddleware(OwinMiddleware next, TimeKeeper timeKeeper, ShutdownNotifier notifier) : base(next)
        {
            this.next = next;
            sampler = new HystrixMetricsSampler(timeKeeper);
            sampler.Start();
            notifier.Register(() =>
            {
                sampler.Stop();
                shutdownTokenSource.Cancel();
            });
        }

        public override async Task Invoke(IOwinContext context)
        {
            if (context.Request.Accept != "text/event-stream")
            {
                context.Response.StatusCode = 400;
                await next.Invoke(context);
                return;
            }

            var owinCallCancelled = context.Get<CancellationToken>("owin.CallCancelled");
            var taskCompletionResult = new TaskCompletionSource<int>();
            var token = CancellationTokenSource.CreateLinkedTokenSource(shutdownTokenSource.Token, owinCallCancelled).Token;

            var sendInterval = GetSendInterval(context);
            using (sampler.SampleData.Sample(sendInterval).ObserveOn(DefaultScheduler.Instance).Subscribe(data => metricsDataQueue.Enqueue(data)))
            using (var task = Task.Run(() => SendStats(context, token, sendInterval), CancellationToken.None))
            using (token.Register(() => CleanUp(taskCompletionResult, task)))
            {
                await taskCompletionResult.Task;
                await next.Invoke(context);
            }
        }

        private void SendStats(IOwinContext context, CancellationToken cancellationToken, TimeSpan sendInterval)
        {
            context.Response.Headers["Content-Type"] = "text/event-stream;charset=UTF-8";
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, max-age=0, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET";
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";

            using (var outputWriter = new StreamWriter(context.Response.Body))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string data;
                    if (metricsDataQueue.TryDequeue(out data))
                    {
                        outputWriter.WriteLine($"data: {data}\n");

                        outputWriter.Flush();

                        continue;
                    }

                    SpinWait.SpinUntil(() => cancellationToken.IsCancellationRequested, sendInterval);
                }
            }
        }

        private static void CleanUp(TaskCompletionSource<int> tcs, Task task)
        {
            task.Wait();

            tcs.SetResult(1);
        }

        private static TimeSpan GetSendInterval(IOwinContext context)
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
    }
}