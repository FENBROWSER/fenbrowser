namespace FenBrowser.Js.Modules;

// ECMA-262 16.2.1.4 [[Status]] of a Cyclic Module Record.
//
// Lifecycle (one-way transitions):
//   Unlinked        : freshly parsed; no module-graph linking attempted yet.
//   Linking         : LinkRequestedModules in progress for this module subtree.
//   Linked          : every dependency reachable from this module has been linked.
//   Evaluating      : top-level code is currently executing (synchronous portion).
//   EvaluatingAsync : top-level await is in flight; subsequent reads from `Evaluate`
//                     resolve when the awaited value settles.
//   Evaluated       : terminal state; either successful or failed (see EvaluationError).
public enum ModuleStatus : byte
{
    Unlinked,
    Linking,
    Linked,
    Evaluating,
    EvaluatingAsync,
    Evaluated,
}
