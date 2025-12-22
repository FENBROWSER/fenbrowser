# FenBrowser Event Loop Semantics Specification

> **Version**: 1.0
> **Status**: AUTHORITATIVE
> **Date**: 2025-12-22

This document defines the exact execution order for JavaScript, events, promises, observers, and rendering in FenBrowser. **All implementations MUST conform to this specification.**

---

## 1. Core Definitions

### 1.1 Task

A **Task** is a discrete unit of work scheduled for execution. Tasks are processed one at a time, in order.

**Task Sources:**

- Input events (mouse, keyboard, touch, pointer)
- Timer callbacks (`setTimeout`, `setInterval`)
- Fetch completion callbacks
- `postMessage` delivery
- IndexedDB operation callbacks
- Worker message delivery
- `requestIdleCallback`

### 1.2 Microtask

A **Microtask** is a lightweight callback that runs to completion before the next task.

**Microtask Sources:**

- `Promise.then()`, `.catch()`, `.finally()` reactions
- `queueMicrotask()` calls
- `MutationObserver` callbacks (after microtask checkpoint)

### 1.3 Microtask Checkpoint

A point in execution where all pending microtasks are drained before proceeding.

---

## 2. Execution Order (LOCKED)

```
┌─────────────────────────────────────────────────────────────┐
│                    EVENT LOOP ITERATION                      │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ STEP 1: Dequeue Task                                  │   │
│  │   - Pick oldest task from TaskQueue                   │   │
│  │   - If no task, wait or skip to rendering             │   │
│  └──────────────────────────────────────────────────────┘   │
│                          │                                   │
│                          ▼                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ STEP 2: Execute Task                                  │   │
│  │   - Run task callback to completion                   │   │
│  │   - May enqueue microtasks                            │   │
│  └──────────────────────────────────────────────────────┘   │
│                          │                                   │
│                          ▼                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ STEP 3: Microtask Checkpoint                          │   │
│  │   - Drain ALL microtasks before proceeding            │   │
│  │   - Each microtask may enqueue more microtasks        │   │
│  │   - Continue until microtask queue is empty           │   │
│  └──────────────────────────────────────────────────────┘   │
│                          │                                   │
│                          ▼                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ STEP 4: DOM Mutation Flush                            │   │
│  │   - Apply batched DOM mutations                       │   │
│  │   - Deliver MutationObserver records                  │   │
│  │   - Microtask checkpoint after callbacks              │   │
│  └──────────────────────────────────────────────────────┘   │
│                          │                                   │
│                          ▼                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ STEP 5: Rendering Update (if needed)                  │   │
│  │   a) Check if layout is dirty                         │   │
│  │   b) If dirty:                                        │   │
│  │      - Measure                                        │   │
│  │      - Layout                                         │   │
│  │      - Paint                                          │   │
│  │   c) NO JAVASCRIPT EXECUTION during this step         │   │
│  └──────────────────────────────────────────────────────┘   │
│                          │                                   │
│                          ▼                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ STEP 6: Observer Evaluation                           │   │
│  │   a) ResizeObserver callbacks                         │   │
│  │      - Microtask checkpoint                           │   │
│  │   b) IntersectionObserver callbacks                   │   │
│  │      - Microtask checkpoint                           │   │
│  └──────────────────────────────────────────────────────┘   │
│                          │                                   │
│                          ▼                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ STEP 7: Animation Frame Callbacks                     │   │
│  │   - Execute requestAnimationFrame callbacks           │   │
│  │   - Microtask checkpoint after each callback          │   │
│  └──────────────────────────────────────────────────────┘   │
│                          │                                   │
│                          ▼                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ STEP 8: Return to STEP 1                              │   │
│  └──────────────────────────────────────────────────────┘   │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

---

## 3. Invariants (MUST NOT VIOLATE)

### 3.1 Phase Purity

**JavaScript MUST NOT execute during Measure, Layout, or Paint.**

Violations are considered bugs and must be fixed immediately.

### 3.2 Microtask Exhaustion

**All microtasks MUST be drained at each checkpoint before proceeding.**

A microtask checkpoint continues until the microtask queue is empty.

### 3.3 Task Ordering

**Tasks from the same source MUST execute in FIFO order.**

### 3.4 No Reentrancy

**A task callback MUST NOT synchronously trigger another task.**

All scheduling is through the queue.

### 3.5 Observer Timing

**Observers fire AFTER layout, not during.**

ResizeObserver and IntersectionObserver callbacks receive snapshot data.

---

## 4. API Contracts

### 4.1 TaskQueue

```csharp
public interface ITaskQueue
{
    void Enqueue(ScheduledTask task);
    ScheduledTask Dequeue();
    bool HasPendingTasks { get; }
    int Count { get; }
}
```

### 4.2 MicrotaskQueue

```csharp
public interface IMicrotaskQueue
{
    void Enqueue(Action microtask);
    void DrainAll();
    bool HasPendingMicrotasks { get; }
}
```

### 4.3 EventLoopCoordinator

```csharp
public interface IEventLoopCoordinator
{
    void ScheduleTask(Action callback, TaskSource source);
    void ScheduleMicrotask(Action callback);
    void ProcessRenderingUpdate();
    void NotifyLayoutDirty();
    EnginePhase CurrentPhase { get; }
}
```

---

## 5. TaskSource Enumeration

```csharp
public enum TaskSource
{
    UserInteraction,    // Mouse, keyboard, touch
    Timer,              // setTimeout, setInterval
    Networking,         // fetch, XHR
    Messaging,          // postMessage
    IndexedDB,          // IDB callbacks
    DOMManipulation,    // Script-triggered DOM changes
    History,            // Navigation events
    Other
}
```

---

## 6. Phase Assertions

At any point, the engine MUST be in exactly one phase:

```csharp
public enum EnginePhase
{
    Idle,           // No work in progress
    JSExecution,    // Running JavaScript
    Microtasks,     // Draining microtask queue
    DOMFlush,       // Applying DOM mutations
    Measure,        // Measuring elements
    Layout,         // Computing layout
    Paint,          // Rendering to canvas
    Observers,      // Running observer callbacks
    Animation       // requestAnimationFrame
}
```

**Transitions:**

- `JSExecution` → `Microtasks` (always after task completes)
- `Microtasks` → `DOMFlush` (after microtasks drained)
- `Measure` → `Layout` → `Paint` (sequential, no interruption)
- `Observers` → `Microtasks` (after each observer batch)

---

## 7. Test Requirements

The following scenarios MUST be tested:

1. **Microtask before next task**

   - Schedule task A, schedule microtask M during A
   - M MUST complete before task B starts

2. **Promise ordering**

   - `Promise.resolve().then(A).then(B)`
   - A fires before B

3. **No JS during paint**

   - Any JS execution during Paint phase = test failure

4. **Observer after layout**

   - ResizeObserver callback receives final layout dimensions

5. **Mutation batching**
   - Multiple DOM changes = single MutationObserver callback

---

## 8. Compliance Checklist

- [ ] TaskQueue implemented with correct ordering
- [ ] MicrotaskQueue drains completely at checkpoints
- [ ] EnginePhase tracked and asserted
- [ ] No JS execution during Measure/Layout/Paint
- [ ] Observers fire with snapshot data
- [ ] All test scenarios pass

---

**Document Status**: LOCKED
**Last Updated**: 2025-12-22
