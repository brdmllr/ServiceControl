﻿namespace ServiceControl.Infrastructure
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using NServiceBus.Logging;

    public class TimeKeeper : IDisposable
    {
        ConcurrentDictionary<Timer, object> timers = new ConcurrentDictionary<Timer, object>();
        private ILog log = LogManager.GetLogger<TimeKeeper>();
        private bool disposed;
        private int disposeSignaled;

        public Timer NewTimer(Func<bool> callback, TimeSpan dueTime, TimeSpan period)
        {
            ThrowIfDisposed();

            Timer timer = null;

            timer = new Timer(_ =>
            {
                var reschedule = false;

                try
                {
                    reschedule = callback();
                }
                catch (Exception ex)
                {
                    log.Error("Reoccurring timer task failed.", ex);
                }
                if (reschedule && timers.ContainsKey(timer))
                {
                    try
                    {
                        timer.Change(period, Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                        // timer has been disposed already, safe to ignore
                    }
                }
            }, null, dueTime, Timeout.InfiniteTimeSpan);

            timers.TryAdd(timer, null);
            return timer;
        }

        public Timer New(Action callback, TimeSpan dueTime, TimeSpan period)
        {
            ThrowIfDisposed();

            return NewTimer(() =>
            {
                callback();
                return true;
            }, dueTime, period);
        }

        public void Release(Timer timer)
        {
            ThrowIfDisposed();

            object _;
            timers.TryRemove(timer, out _);
            WaitAndDispose(timer);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeSignaled, 1) != 0)
            {
                return;
            }

            foreach (var pair in timers)
            {
                WaitAndDispose(pair.Key);
            }

            disposed = true;
        }

        private static void WaitAndDispose(Timer timer)
        {
            using (var manualResetEvent = new ManualResetEvent(false))
            {
                timer.Dispose(manualResetEvent);
                manualResetEvent.WaitOne();
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("TimeKeeper");
            }
        }
    }
}