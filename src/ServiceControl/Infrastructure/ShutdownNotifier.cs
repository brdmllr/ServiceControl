namespace ServiceControl.Infrastructure
{
    using System;
    using System.Threading;

    public class ShutdownNotifier : IDisposable
    {
        CancellationTokenSource source = new CancellationTokenSource();

        public CancellationTokenRegistration Register(Action callback)
        {
            return source.Token.Register(callback);
        }

        public void Dispose()
        {
            source.Cancel();
            source.Dispose();
        }
    }
}
