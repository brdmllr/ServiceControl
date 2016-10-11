namespace ServiceControl.Operations
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Raven.Abstractions.Commands;
    using Raven.Client;

    /// <summary>
    ///     This class allows performing high throughput document store operations, using a batching mechanism
    ///     That mechanism collects up to 128 documents into a single batch and stores them in a single operation, reducing
    ///     network io compared to an atomic operation
    ///     The mechanism is also capable of treating single store operation failure by retrying a batch, executing it item by
    ///     item
    /// </summary>
    public class RavenBatchOptimizer : IDisposable
    {
        private readonly CancellationToken _ct;
        private readonly ConcurrentQueue<ManualResetEventSlim> _mresQueue = new ConcurrentQueue<ManualResetEventSlim>();
        private readonly BlockingCollection<Work> _pending = new BlockingCollection<Work>();
        private readonly IDocumentStore _store;
        private readonly CancellationTokenSource _cts;
        private List<Task> _proccessingTasks;

        public RavenBatchOptimizer(IDocumentStore store, CancellationToken ct, int parallelism = 2)
        {
            _store = store;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ct = _cts.Token;

            if (_proccessingTasks == null)
            {
                _proccessingTasks = new List<Task>();
                for (var i = 0; i < parallelism; i++)
                {
                    _proccessingTasks.Add(Task.Factory.StartNew(DoWork, TaskCreationOptions.LongRunning));
                }
            }
        }

        public void Dispose()
        {
            if ((_proccessingTasks != null) && (_cts.IsCancellationRequested == false))
            {
                var proccesses = _proccessingTasks;
                _proccessingTasks = null;
                _cts.Cancel();
                _pending.CompleteAdding();
                try
                {
                    Task.WaitAll(proccesses.ToArray());
                }
                catch (OperationCanceledException)
                {
                    // do nothing
                }
            }
        }

        /// <summary>
        ///     Enqueue the entity to be stored and waits until it will be proccessed
        /// </summary>
        public void Write(object entity)
        {
            _cts.Token.ThrowIfCancellationRequested();
            ManualResetEventSlim mres;
            if (_mresQueue.TryDequeue(out mres) == false)
            {
                mres = new ManualResetEventSlim();
            }
            var work = new Work
            {
                Entity = entity,
                Event = mres
            };

            work.Event.Reset();
            _pending.Add(work);

            // wait for entity to be stored in the next batch
            work.Event.Wait();
            _mresQueue.Enqueue(mres);

            if (work.Exception != null)
            {
                throw new InvalidOperationException("Failed to write", work.Exception);
            }
        }

        /// <summary>
        ///     Enqueue the entity to be stored and waits until it will be proccessed
        /// </summary>
        public void Write(ICommandData[] commands)
        {
            _cts.Token.ThrowIfCancellationRequested();
            ManualResetEventSlim mres;
            if (_mresQueue.TryDequeue(out mres) == false)
            {
                mres = new ManualResetEventSlim();
            }
            var work = new Work
            {
                Commands = commands,
                Event = mres
            };

            work.Event.Reset();
            _pending.Add(work);

            // wait for entity to be stored in the next batch
            work.Event.Wait();
            _mresQueue.Enqueue(mres);

            if (work.Exception != null)
            {
                throw new InvalidOperationException("Failed to write", work.Exception);
            }
        }

        /// <summary>
        ///     Long running method that performs the batch grouping and execution operations
        /// </summary>
        /// <returns></returns>
        private async Task DoWork()
        {
            var list = new List<Work>();

            while ((_cts.IsCancellationRequested == false) || (_pending.IsCompleted == false))
            {
                list.Clear();
                var work = _pending.Take(_ct);
                list.Add(work);
                Work item;

                while ((list.Count < 128) && _pending.TryTake(out item))
                {
                    list.Add(item);
                }
                using (var session = _store.OpenAsyncSession())
                {
                    var failed = false;

                    try
                    {
                        foreach (var p in list)
                        {
                            if (p.Entity != null)
                            {
                                session.Advanced.Store(p.Entity);
                            }

                            if (p.Commands != null && p.Commands.Length > 0)
                            {
                                session.Advanced.Defer(p.Commands);
                            }
                        }
                        
                        await session.SaveChangesAsync();
                    }
                    catch (Exception)
                    {
                        failed = true;
                    }

                    // if we've failed to store the batch (which was entirely rolled back), we try executing each of the items seperately,
                    // in case we've failed because of a single item
                    if (failed)
                    {
                        await HandleSaveFailure(list);
                    }
                }
                foreach (var p in list)
                {
                    p.Event.Set();
                }
            }
        }

        /// <summary>
        ///     Store entities list one by one, setting the errorous message state to the particular message
        /// </summary>
        /// <param name="list"></param>
        /// <returns></returns>
        private async Task HandleSaveFailure(List<Work> list)
        {
            foreach (var p in list)
            {
                using (var recoverySession = _store.OpenAsyncSession())
                {
                    await recoverySession.StoreAsync(p.Entity);
                    try
                    {
                        await recoverySession.SaveChangesAsync();
                    }
                    catch (Exception e)
                    {
                        p.Exception = e;
                    }
                }
            }
        }

        private class Work
        {
            public object Entity;
            public ICommandData[] Commands { get; set; }

            public ManualResetEventSlim Event;
            public Exception Exception;
        }
    }
}