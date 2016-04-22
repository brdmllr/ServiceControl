namespace Hystrix.Dashboard
{
    using Hystrix.MetricsEventStream;
    using Owin;

    public static class HystrixExtension
    {
        public static IAppBuilder UseHystrixStreamer(this IAppBuilder app, params object[] args)
        {
            app.Use<HystrixStreamerMiddleware>(args);
            return app;
        }
    }
}
