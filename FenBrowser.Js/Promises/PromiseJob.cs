using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Promises;

// ECMA-262 27.2.2 Promise Jobs. A Promise Job is an Abstract Closure with no
// parameters that initiates the asynchronous resolution of a Promise. Promise Jobs are
// scheduled with HostEnqueuePromiseJob.
//
// The data-only model: each job subclass carries the inputs it needs; nothing here
// runs JS. The interpreter is the layer that decides how to execute a dequeued job,
// which keeps the Promises module independent of any concrete interpreter
// implementation (and lets unit tests drive the queue without an isolate).
public abstract class PromiseJob
{
    protected PromiseJob(int realmId)
    {
        RealmId = realmId;
    }

    // 9.5 step 4 "If realm is not null, queueing context is added to" - the realm
    // captured at enqueue time determines which realm the job runs in when dequeued.
    public int RealmId { get; }
}

// 27.2.2.1 NewPromiseReactionJob ( reaction, argument ). Carries one reaction plus the
// value it should be invoked with (the settled promise value or rejection reason).
public sealed class PromiseReactionJob : PromiseJob
{
    public PromiseReactionJob(PromiseReaction reaction, JsValue argument, int realmId)
        : base(realmId)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        Reaction = reaction;
        Argument = argument;
    }

    public PromiseReaction Reaction { get; }
    public JsValue Argument { get; }
}

// 27.2.2.2 NewPromiseResolveThenableJob ( promiseToResolve, thenable, then ). Used
// when a promise resolves with a value that has a `then` method - the spec defers the
// .then(...) call to a job so that the resolution observer sees the same ordering as
// for a real Promise.
public sealed class PromiseResolveThenableJob : PromiseJob
{
    public PromiseResolveThenableJob(
        JsValue promiseToResolve,
        JsValue thenable,
        JsValue then,
        int realmId)
        : base(realmId)
    {
        PromiseToResolve = promiseToResolve;
        Thenable = thenable;
        Then = then;
    }

    public JsValue PromiseToResolve { get; }
    public JsValue Thenable { get; }
    public JsValue Then { get; }
}
