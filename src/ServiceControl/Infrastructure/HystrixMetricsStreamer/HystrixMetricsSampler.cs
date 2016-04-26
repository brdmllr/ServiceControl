namespace Hystrix.MetricsEventStream
{
    using System;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Threading;
    using Netflix.Hystrix;
    using Netflix.Hystrix.CircuitBreaker;
    using Netflix.Hystrix.ThreadPool;
    using Netflix.Hystrix.Util;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using ServiceControl.Infrastructure;

    class HystrixMetricsSampler: IDisposable
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0);
        private readonly TimeKeeper timeKeeper;
        private Subject<string> subject = new Subject<string>();

        private Timer timer;

        public HystrixMetricsSampler(TimeKeeper timeKeeper)
        {
            this.timeKeeper = timeKeeper;
        }

        public IObservable<string> SampleData => subject.AsObservable();

        private void DoWork()
        {
            if (!subject.HasObservers)
            {
                return;
            }

            foreach (var commandMetrics in HystrixCommandMetrics.Instances)
            {
                subject.OnNext(CreateCommandSampleData(commandMetrics));
            }

            foreach (var threadPoolMetrics in HystrixThreadPoolMetrics.Instances)
            {
                subject.OnNext(CreateThreadPoolSampleData(threadPoolMetrics));
            }
        }

        private static string CreateCommandSampleData(HystrixCommandMetrics commandMetrics)
        {
            var circuitBreaker = HystrixCircuitBreakerFactory.GetInstance(commandMetrics.CommandKey);
            var healthCounts = commandMetrics.GetHealthCounts();
            var commandProperties = commandMetrics.Properties;

            var data = new JObject(
                new JProperty("type", "HystrixCommand"),
                new JProperty("name", commandMetrics.CommandKey.Name),
                new JProperty("group", commandMetrics.CommandGroup.Name),
                new JProperty("currentTime", GetCurrentTimeForJavascript()),
                circuitBreaker == null ? new JProperty("isCircuitBreakerOpen", false) : new JProperty("isCircuitBreakerOpen", circuitBreaker.IsOpen()),
                new JProperty("errorPercentage", healthCounts.ErrorPercentage), // health counts
                new JProperty("errorCount", healthCounts.ErrorCount),
                new JProperty("requestCount", healthCounts.TotalRequests),
                new JProperty("rollingCountCollapsedRequests", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.Collapsed)), // rolling counters
                new JProperty("rollingCountExceptionsThrown", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.ExceptionThrown)),
                new JProperty("rollingCountFailure", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.Failure)),
                new JProperty("rollingCountFallbackFailure", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.FallbackFailure)),
                new JProperty("rollingCountFallbackRejection", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.FallbackRejection)),
                new JProperty("rollingCountFallbackSuccess", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.FallbackSuccess)),
                new JProperty("rollingCountResponsesFromCache", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.ResponseFromCache)),
                new JProperty("rollingCountSemaphoreRejected", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.SemaphoreRejected)),
                new JProperty("rollingCountShortCircuited", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.ShortCircuited)),
                new JProperty("rollingCountSuccess", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.Success)),
                new JProperty("rollingCountThreadPoolRejected", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.ThreadPoolRejected)),
                new JProperty("rollingCountTimeout", commandMetrics.GetRollingCount(HystrixRollingNumberEvent.Timeout)),
                new JProperty("currentConcurrentExecutionCount", commandMetrics.CurrentConcurrentExecutionCount),
                new JProperty("latencyExecute_mean", commandMetrics.GetExecutionTimeMean()), // latency percentiles
                new JProperty(
                    "latencyExecute",
                    new JObject(
                        new JProperty("0", commandMetrics.GetExecutionTimePercentile(0)),
                        new JProperty("25", commandMetrics.GetExecutionTimePercentile(25)),
                        new JProperty("50", commandMetrics.GetExecutionTimePercentile(50)),
                        new JProperty("75", commandMetrics.GetExecutionTimePercentile(75)),
                        new JProperty("90", commandMetrics.GetExecutionTimePercentile(90)),
                        new JProperty("95", commandMetrics.GetExecutionTimePercentile(95)),
                        new JProperty("99", commandMetrics.GetExecutionTimePercentile(99)),
                        new JProperty("99.5", commandMetrics.GetExecutionTimePercentile(99.5)),
                        new JProperty("100", commandMetrics.GetExecutionTimePercentile(100)))),
                new JProperty("latencyTotal_mean", commandMetrics.GetTotalTimeMean()),
                new JProperty(
                    "latencyTotal",
                    new JObject(
                        new JProperty("0", commandMetrics.GetTotalTimePercentile(0)),
                        new JProperty("25", commandMetrics.GetTotalTimePercentile(25)),
                        new JProperty("50", commandMetrics.GetTotalTimePercentile(50)),
                        new JProperty("75", commandMetrics.GetTotalTimePercentile(75)),
                        new JProperty("90", commandMetrics.GetTotalTimePercentile(90)),
                        new JProperty("95", commandMetrics.GetTotalTimePercentile(95)),
                        new JProperty("99", commandMetrics.GetTotalTimePercentile(99)),
                        new JProperty("99.5", commandMetrics.GetTotalTimePercentile(99.5)),
                        new JProperty("100", commandMetrics.GetTotalTimePercentile(100)))),
                new JProperty("propertyValue_circuitBreakerRequestVolumeThreshold", commandProperties.CircuitBreakerRequestVolumeThreshold.Get()), // property values for reporting what is actually seen by the command rather than what was set somewhere 
                new JProperty("propertyValue_circuitBreakerSleepWindowInMilliseconds", (long) commandProperties.CircuitBreakerSleepWindow.Get().TotalMilliseconds),
                new JProperty("propertyValue_circuitBreakerErrorThresholdPercentage", commandProperties.CircuitBreakerErrorThresholdPercentage.Get()),
                new JProperty("propertyValue_circuitBreakerForceOpen", commandProperties.CircuitBreakerForceOpen.Get()),
                new JProperty("propertyValue_circuitBreakerForceClosed", commandProperties.CircuitBreakerForceClosed.Get()),
                new JProperty("propertyValue_circuitBreakerEnabled", commandProperties.CircuitBreakerEnabled.Get()),
                new JProperty("propertyValue_executionIsolationStrategy", commandProperties.ExecutionIsolationStrategy.Get()),
                new JProperty("propertyValue_executionIsolationThreadTimeoutInMilliseconds", (long) commandProperties.ExecutionIsolationThreadTimeout.Get().TotalMilliseconds),
                new JProperty("propertyValue_executionIsolationThreadInterruptOnTimeout", commandProperties.ExecutionIsolationThreadInterruptOnTimeout.Get()),
                new JProperty("propertyValue_executionIsolationThreadPoolKeyOverride", commandProperties.ExecutionIsolationThreadPoolKeyOverride.Get()),
                new JProperty("propertyValue_executionIsolationSemaphoreMaxConcurrentRequests", commandProperties.ExecutionIsolationSemaphoreMaxConcurrentRequests.Get()),
                new JProperty("propertyValue_fallbackIsolationSemaphoreMaxConcurrentRequests", commandProperties.FallbackIsolationSemaphoreMaxConcurrentRequests.Get()),
                new JProperty("propertyValue_metricsRollingStatisticalWindowInMilliseconds", commandProperties.MetricsRollingStatisticalWindowInMilliseconds.Get()),
                new JProperty("propertyValue_requestCacheEnabled", commandProperties.RequestCacheEnabled.Get()),
                new JProperty("propertyValue_requestLogEnabled", commandProperties.RequestLogEnabled.Get()),
                new JProperty("reportingHosts", 1));

            return data.ToString(Formatting.None);
        }

        private static string CreateThreadPoolSampleData(HystrixThreadPoolMetrics threadPoolMetrics)
        {
            var properties = threadPoolMetrics.Properties;

            var data = new JObject(
                new JProperty("type", "HystrixThreadPool"),
                new JProperty("name", threadPoolMetrics.ThreadPoolKey.Name),
                new JProperty("currentTime", GetCurrentTimeForJavascript()),
                new JProperty("currentActiveCount", threadPoolMetrics.CurrentActiveCount),
                new JProperty("currentCompletedTaskCount", threadPoolMetrics.CurrentCompletedTaskCount),
                new JProperty("currentCorePoolSize", threadPoolMetrics.CurrentCorePoolSize),
                new JProperty("currentLargestPoolSize", threadPoolMetrics.CurrentLargestPoolSize),
                new JProperty("currentMaximumPoolSize", threadPoolMetrics.CurrentMaximumPoolSize),
                new JProperty("currentPoolSize", threadPoolMetrics.CurrentPoolSize),
                new JProperty("currentQueueSize", threadPoolMetrics.CurrentQueueSize),
                new JProperty("currentTaskCount", threadPoolMetrics.CurrentTaskCount),
                new JProperty("rollingCountThreadsExecuted", threadPoolMetrics.RollingCountThreadsExecuted),
                new JProperty("rollingMaxActiveThreads", threadPoolMetrics.RollingMaxActiveThreads),
                new JProperty("propertyValue_queueSizeRejectionThreshold", properties.QueueSizeRejectionThreshold.Get()),
                new JProperty("propertyValue_metricsRollingStatisticalWindowInMilliseconds", properties.MetricsRollingStatisticalWindowInMilliseconds.Get()),
                new JProperty("reportingHosts", 1));

            return data.ToString(Formatting.None);
        }

        private static long GetCurrentTimeForJavascript()
        {
            return (long) (DateTime.UtcNow - Epoch).TotalMilliseconds;
        }

        public void Start()
        {
            timer = timeKeeper.New(DoWork, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
        }

        public void Stop()
        {
            timeKeeper.Release(timer);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}