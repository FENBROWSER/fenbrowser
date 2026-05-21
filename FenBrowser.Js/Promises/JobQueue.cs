namespace FenBrowser.Js.Promises;

// 9.5 Jobs and Host Operations to Enqueue Jobs.
//
// A FIFO queue of pending PromiseJobs awaiting a microtask checkpoint. The host (here
// the FenBrowser event loop or the standalone shell) calls RunMicrotaskCheckpoint at
// the right point in its loop; this class is intentionally I/O free so it can be
// driven from tests without an interpreter.
public sealed class JobQueue
{
    private readonly Queue<PromiseJob> _jobs = new();

    public int Count => _jobs.Count;

    public void Enqueue(PromiseJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobs.Enqueue(job);
    }

    public bool TryDequeue(out PromiseJob? job)
    {
        if (_jobs.Count == 0)
        {
            job = null;
            return false;
        }

        job = _jobs.Dequeue();
        return true;
    }

    // 8.4 PerformMicrotaskCheckpoint: drain the queue, invoking the supplied runner
    // for each dequeued job. The runner returns false to abort the checkpoint (e.g.
    // a fatal error inside the interpreter); pending jobs remain in the queue for the
    // next checkpoint. Returns the number of jobs actually executed.
    //
    // Per the spec the checkpoint runs jobs until the queue is empty - including jobs
    // enqueued by earlier jobs in the same checkpoint. This implementation honors
    // that: each iteration re-checks Count rather than snapshotting.
    public int RunMicrotaskCheckpoint(Func<PromiseJob, bool> runJob)
    {
        ArgumentNullException.ThrowIfNull(runJob);

        var ran = 0;
        while (_jobs.Count > 0)
        {
            var job = _jobs.Dequeue();
            if (!runJob(job))
            {
                return ran;
            }

            ran++;
        }

        return ran;
    }
}
