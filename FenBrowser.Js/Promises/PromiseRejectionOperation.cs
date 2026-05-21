namespace FenBrowser.Js.Promises;

// ECMA-262 27.2.1.9 HostPromiseRejectionTracker ( promise, operation ).
//
// The host hook receives one of two operations:
//   Reject : the promise was just rejected; no handler is attached yet. The host may
//            record it as a possibly-unhandled rejection and surface a warning later
//            if no handler arrives.
//   Handle : a previously-rejected unhandled promise just gained a handler. The host
//            should withdraw any prior "unhandled rejection" warning.
public enum PromiseRejectionOperation : byte
{
    Reject,
    Handle,
}
