namespace FenBrowser.Js.Promises;

using FenBrowser.Js.Heap;

// 9.5 Jobs and Host Operations to Enqueue Jobs.
//
// A FIFO queue of pending PromiseJobs awaiting a microtask checkpoint. Hosted
// embedders may enqueue jobs from completion callbacks that are not running on the
// JS thread, so queue mutation is synchronized. Jobs themselves are always executed
// outside the queue lock.
//
// Pending jobs are also heap roots: they may be the only owners of a settled value,
// reaction handler/capability, thenable, or promise while waiting for the next
// microtask checkpoint. The interpreter/realm must register this queue with JsHeap.
public sealed class JobQueue : IHeapRootSource
{
    private readonly Queue<PromiseJob> _jobs = new();
    private readonly object _sync = new();
    private PromiseJob? _runningJob;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _jobs.Count;
            }
        }
    }

    public void Trace(IHeapTracer tracer)
    {
        TraceRoots(tracer);
    }

    public void Enqueue(PromiseJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_sync)
        {
            _jobs.Enqueue(job);
        }
    }

    public bool TryDequeue(out PromiseJob? job)
    {
        lock (_sync)
        {
            if (_jobs.Count == 0)
            {
                job = null;
                return false;
            }

            job = _jobs.Dequeue();
            return true;
        }
    }

    public void TraceRoots(IHeapTracer tracer)
    {
        ArgumentNullException.ThrowIfNull(tracer);

        lock (_sync)
        {
            _runningJob?.Trace(tracer);
            foreach (var job in _jobs)
            {
                job.Trace(tracer);
            }
        }
    }

    // 8.4 PerformMicrotaskCheckpoint: drain the queue, invoking the supplied runner
    // for each dequeued job. The runner returns false to abort the checkpoint (e.g.
    // a fatal error inside the interpreter); pending jobs remain in the queue for the
    // next checkpoint.
    //
    // Each iteration dequeues under the lock, then runs the job without the lock.
    // This preserves FIFO order and lets the running job (or another host thread)
    // enqueue more microtasks that are observed by the same checkpoint.
    public int RunMicrotaskCheckpoint(Func<PromiseJob, bool> runJob)
    {
        ArgumentNullException.ThrowIfNull(runJob);

        var ran = 0;
        while (true)
        {
            PromiseJob job;
            lock (_sync)
            {
                if (_jobs.Count == 0)
                {
                    break;
                }

                job = _jobs.Dequeue();
                _runningJob = job;
            }

            try
            {
                if (!runJob(job))
                {
                    return ran;
                }

                ran++;
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_runningJob, job))
                    {
                        _runningJob = null;
                    }
                }
            }
        }

        return ran;
    }
}
