using System.Reflection;
using System.Reflection.Emit;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Jit.CacheIR;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using static FenBrowser.Js.Jit.Baseline.JsRuntimeBindings;
using OpCode = FenBrowser.Js.Bytecode.OpCode;

#if !PUBLISH_AOT
namespace FenBrowser.Js.Jit.Baseline;

/// <summary>
/// Compiles a bytecode function straight to IL.
/// </summary>
/// <remarks>
/// The compiled body shares the interpreter's frame: registers, environment and
/// instruction pointer all stay where the dispatch loop keeps them. A running
/// frame can therefore hand over at a loop header and a routed throw can hand
/// back, with no state to reconcile in either direction.
/// </remarks>
internal static class BaselineCompiler
{
    private static readonly Type[] Signature =
        [typeof(BytecodeInterpreter), typeof(InterpreterFrame), typeof(int)];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<OpCode, int> Refusals = new();

    internal static long Compiled;

    /// <summary>Opcodes that turned a function away, most frequent first.</summary>
    internal static string DescribeRefusals()
    {
        var ordered = Refusals.ToArray();
        Array.Sort(ordered, (x, y) => y.Value.CompareTo(x.Value));
        return ordered.Length == 0
            ? "none"
            : string.Join(" ", ordered.Select(entry => entry.Key + "=" + entry.Value.ToString()));
    }

    internal static JitCompiler.JitDelegate? TryCompile(BytecodeFunction function)
    {
        var instructions = function.InstructionArray;
        if (instructions.Length == 0) return null;

        var method = new DynamicMethod(
            "fenjs_baseline",
            typeof(JsValue),
            Signature,
            typeof(BytecodeInterpreter),
            skipVisibility: true);

        var writer = new FunctionWriter(function, instructions, new ILEmitter(method.GetILGenerator()));
        if (!writer.TryWrite()) return null;

        function.BaselinePool = writer.Pool;
        function.OsrEntryPoints = writer.LoopHeaders.Count > 0 ? writer.LoopHeaders : null;
        Interlocked.Increment(ref Compiled);
        return method.CreateDelegate<JitCompiler.JitDelegate>();
    }

    internal static void Refuse(OpCode op) => Refusals.AddOrUpdate(op, 1, (_, n) => n + 1);

    /// <summary>Emission state for one function.</summary>
    private sealed class FunctionWriter(BytecodeFunction function, Instruction[] instructions, ILEmitter il)
    {
        private static readonly Type BindingType = BindingArrayType.GetElementType()!;

        private readonly List<string> _names = [];
        private readonly List<CacheIRSite> _sites = [];

        private LocalBuilder _registers = null!, _constants = null!, _namePool = null!, _sitePool = null!;
        private LocalBuilder _slotBindings = null!, _resumeIp = null!, _returnValue = null!;
        private LocalBuilder _lhs = null!, _rhs = null!, _environment = null!, _slot = null!;
        private LocalBuilder _receiver = null!, _program = null!, _shape = null!, _cached = null!;

        // One CLR local per register, so an operand is a local rather than a
        // bounds-checked read of a 24-byte struct out of the frame's array.
        private LocalBuilder[] _registerLocals = [];
        private RegisterLiveness _liveness = null!;
        private readonly HashSet<int> _handlerTargets = [];

        // Registers this opcode's own emission has already left in a local. The
        // frame is stale for those until the next spill, so reading them back
        // after a call would undo the write.
        private readonly List<int> _localWrites = [];

        private Label[] _labels = [];
        private Label _dispatch, _exit;
        private bool _routable;

        internal BaselinePool Pool { get; private set; } = null!;

        internal HashSet<int> LoopHeaders { get; } = [];

        internal bool TryWrite()
        {
            foreach (var instruction in instructions)
            {
                // A suspending opcode leaves the frame mid-flight, and compiled
                // code has no way to save and restore an instruction pointer
                // across the delegate boundary.
                if (instruction.OpCode is OpCode.Yield or OpCode.YieldStar
                    or OpCode.Await or OpCode.EnumerateValuesAsync)
                {
                    Refuse(instruction.OpCode);
                    return false;
                }
            }

            var resumePoints = CollectResumePoints();
            _routable = resumePoints.Count > 0;
            var charges = ChargeLoopHeaders();
            _liveness = RegisterLiveness.Compute(instructions, function.RegisterCount, _handlerTargets);

            DeclareLocals();
            EmitPrologue();

            _dispatch = il.DefineLabel();
            il.Mark(_dispatch);
            // A frame entering compiled code after the dispatch loop routed a
            // throw would otherwise see the flag left over from that.
            il.Arg(1);
            il.Int(0);
            il.StoreField(FiThrowRouted);
            // The handler restores the environment it was pushed with, so slot
            // storage derived before a throw no longer describes the frame.
            EmitRefreshSlots();

            _exit = il.BeginTry();
            EmitResumeDispatch(resumePoints);

            for (var ip = 0; ip < instructions.Length; ip++)
            {
                il.Mark(_labels[ip]);

                // Polling at loop headers rather than before every instruction:
                // two calls ahead of each opcode cost more than most opcodes do,
                // and a script that fails to terminate always goes round a loop.
                // Each header is charged for the span it governs so the budget
                // still measures work rather than iterations.
                if (ip == 0 || charges.ContainsKey(ip))
                {
                    // The budget check collects at a safe point, so the frame has
                    // to hold everything the tracer needs before it runs.
                    SpillLiveIn(ip);
                    il.Arg(0);
                    il.Int(charges.TryGetValue(ip, out var charge) ? charge : 1);
                    il.Call(MiCheckExecutionBudgetCharged);
                }

                var instruction = instructions[ip];
                var safepoint = IsSafepoint(instruction.OpCode);
                _localWrites.Clear();
                if (safepoint) SpillLiveIn(ip);
                if (!TryEmit(instruction, ip))
                {
                    Refuse(instruction.OpCode);
                    return false;
                }

                if (safepoint) ReloadLiveOut(ip);

                EmitRoutedThrowCheck(instruction.OpCode);
            }

            // Falling off the end yields undefined, as the dispatch loop does.
            il.Get(PiJsValueUndefined);
            il.Store(_returnValue);
            il.Branch(OpCodes.Leave, _exit);

            EmitCatch();
            il.EndTry();

            il.Load(_returnValue);
            il.Op(OpCodes.Ret);

            Pool = new BaselinePool(
                function.Constants as JsValue[] ?? [.. function.Constants],
                [.. _names],
                [.. _sites]);
            return true;
        }

        // ---- structure -------------------------------------------------

        private HashSet<int> CollectResumePoints()
        {
            var resumePoints = new HashSet<int>();
            for (var ip = 0; ip < instructions.Length; ip++)
            {
                var instruction = instructions[ip];
                switch (instruction.OpCode)
                {
                    // A backwards branch target is where a running frame may
                    // transfer in: every register the code reads lives in the
                    // frame both sides share.
                    case OpCode.Jump when instruction.A <= ip && InRange(instruction.A):
                        LoopHeaders.Add(instruction.A);
                        break;
                    case OpCode.JumpIfFalse when instruction.B <= ip && InRange(instruction.B):
                        LoopHeaders.Add(instruction.B);
                        break;
                    case OpCode.PushHandler:
                        if (InRange(instruction.A)) _handlerTargets.Add(instruction.A);
                        if (InRange(instruction.D)) _handlerTargets.Add(instruction.D);
                        break;
                }
            }

            resumePoints.UnionWith(_handlerTargets);
            resumePoints.UnionWith(LoopHeaders);
            return resumePoints;

            bool InRange(int target) => (uint)target < (uint)instructions.Length;
        }

        private Dictionary<int, int> ChargeLoopHeaders()
        {
            var charges = new Dictionary<int, int>(LoopHeaders.Count);
            foreach (var header in LoopHeaders)
            {
                var furthest = header;
                for (var ip = header; ip < instructions.Length; ip++)
                {
                    var branch = instructions[ip];
                    if ((branch.OpCode == OpCode.Jump && branch.A == header) ||
                        (branch.OpCode == OpCode.JumpIfFalse && branch.B == header))
                    {
                        furthest = ip;
                    }
                }

                charges[header] = Math.Max(1, furthest - header + 1);
            }

            return charges;
        }

        private void DeclareLocals()
        {
            _registers = il.Local(typeof(JsValue[]));
            _constants = il.Local(typeof(JsValue[]));
            _namePool = il.Local(typeof(string[]));
            _sitePool = il.Local(typeof(CacheIRSite[]));
            _slotBindings = il.Local(BindingArrayType);
            _resumeIp = il.Local(typeof(int));
            _returnValue = il.Local(typeof(JsValue));
            _lhs = il.Local(typeof(JsValue));
            _rhs = il.Local(typeof(JsValue));
            _environment = il.Local(typeof(DeclarativeEnvironmentRecord));
            _slot = il.Local(BindingType.MakeByRefType());
            _receiver = il.Local(typeof(JsObject));
            _program = il.Local(typeof(CacheIRProgram));
            _shape = il.Local(typeof(Shape));
            _cached = il.Local(typeof(JsValue));

            _registerLocals = new LocalBuilder[function.RegisterCount];
            for (var r = 0; r < _registerLocals.Length; r++) _registerLocals[r] = il.Local(typeof(JsValue));

            _labels = new Label[instructions.Length];
            for (var ip = 0; ip < _labels.Length; ip++) _labels[ip] = il.DefineLabel();
        }

        private void EmitPrologue()
        {
            il.Arg(1);
            il.Get(PiRegisters);
            il.Store(_registers);

            // One field read reaches every object operand the body indexes.
            var pool = il.Local(typeof(BaselinePool));
            il.Arg(1);
            il.Get(PiFunction);
            il.LoadField(FiBaselinePool);
            il.Store(pool);

            il.Load(pool);
            il.LoadField(FiPoolConstants);
            il.Store(_constants);
            il.Load(pool);
            il.LoadField(FiPoolNames);
            il.Store(_namePool);
            il.Load(pool);
            il.LoadField(FiPoolSites);
            il.Store(_sitePool);

            il.Arg(2);
            il.Store(_resumeIp);
        }

        /// <summary>
        /// Routes an entering frame to where it left off. Each target gets a stub
        /// that reads the registers live there out of the frame first: a frame
        /// arriving here holds its values in the array, and the body reads them
        /// from locals. A back edge reaches the same instruction without passing
        /// through the stub, so a loop pays nothing for this.
        /// </summary>
        private void EmitResumeDispatch(HashSet<int> resumePoints)
        {
            var top = il.DefineLabel();
            var stubs = new List<(int Target, Label Stub)>(resumePoints.Count);
            foreach (var target in resumePoints)
            {
                var stub = il.DefineLabel();
                stubs.Add((target, stub));
                il.Load(_resumeIp);
                il.Int(target);
                il.Branch(OpCodes.Beq, stub);
            }

            // Anything else, including zero, falls through to the top.
            il.Branch(OpCodes.Br, top);
            foreach (var (target, stub) in stubs)
            {
                il.Mark(stub);
                ReloadLiveIn(target);
                il.Branch(OpCodes.Br, _labels[target]);
            }

            il.Mark(top);
            ReloadLiveIn(0);
        }

        /// <summary>
        /// Deriving the frame's slot storage costs a cast, an identity check and
        /// a call; none of it changes while the environment does not.
        /// </summary>
        private void EmitRefreshSlots()
        {
            var owned = il.DefineLabel();
            var done = il.DefineLabel();

            il.Arg(1);
            il.Get(PiFrameEnvironment);
            il.Cast(typeof(DeclarativeEnvironmentRecord));
            il.Store(_environment);
            il.Load(_environment);
            il.Branch(OpCodes.Brtrue, owned);

            il.Null();
            il.Store(_slotBindings);
            il.Branch(OpCodes.Br, done);

            il.Mark(owned);
            il.Load(_environment);
            il.Arg(1);
            il.Get(PiFunction);
            il.Call(MiSlotBindingsFor);
            il.Store(_slotBindings);
            il.Mark(done);
        }

        /// <summary>
        /// Most of what an opcode delegates to can route a throw to a handler in
        /// this frame rather than raising it: the helper moves the instruction
        /// pointer and returns. Without this the body would carry on with the
        /// next instruction and run the rest of the try block as though nothing
        /// had thrown.
        /// </summary>
        private void EmitRoutedThrowCheck(OpCode op)
        {
            if (!_routable || !CanRouteThrow(op)) return;

            var carryOn = il.DefineLabel();
            il.Arg(1);
            il.LoadField(FiThrowRouted);
            il.Branch(OpCodes.Brfalse, carryOn);

            il.Arg(1);
            il.Int(0);
            il.StoreField(FiThrowRouted);
            EmitRedispatch();
            il.Mark(carryOn);
        }

        private static bool CanRouteThrow(OpCode op) => op switch
        {
            OpCode.LoadConst or OpCode.Move or OpCode.Jump or OpCode.JumpIfFalse or
            OpCode.Nop or OpCode.Return or OpCode.Throw or
            OpCode.PushHandler or OpCode.PopHandler => false,
            _ => true,
        };

        private void EmitRedispatch()
        {
            il.Arg(1);
            il.Get(PiFrameInstructionPointer);
            il.Store(_resumeIp);
            il.Branch(OpCodes.Leave, _dispatch);
        }

        /// <summary>
        /// Anything the body throws is offered to this frame's handlers exactly
        /// as the dispatch loop offers it. An interrupt or an exhausted budget is
        /// not the script's to catch, so it leaves without being offered.
        /// </summary>
        /// <remarks>
        /// The decision is an exception filter, as in the dispatch loop's catch
        /// sites (HasHandler): a throw no try in this frame wants passes by
        /// uncaught. Catching and rethrowing it instead runs each rethrow on top
        /// of the stack the first throw still holds, so a stack overflow
        /// unwinding through a deep chain of compiled frames overflowed a second
        /// time and took the process down.
        /// </remarks>
        private void EmitCatch()
        {
            var thrown = il.Local(typeof(JsThrownException));
            var rethrow = il.DefineLabel();
            var decline = il.DefineLabel();
            var decided = il.DefineLabel();

            il.BeginFilter();
            il.Cast(typeof(JsThrownException));
            il.Store(thrown);
            il.Load(thrown);
            il.Branch(OpCodes.Brfalse, decline);
            il.Load(thrown);
            il.Get(PiThrownUncatchable);
            il.Branch(OpCodes.Brtrue, decline);
            il.Arg(1);
            il.Call(MiHasHandler);
            il.Branch(OpCodes.Br, decided);
            il.Mark(decline);
            il.Int(0);
            il.Mark(decided);

            il.BeginFilteredCatch();
            il.Op(OpCodes.Pop);

            il.Arg(0);
            il.Arg(1);
            il.Load(thrown);
            il.Get(PiThrownValue);
            il.Call(MiRouteThrow);
            il.Branch(OpCodes.Brfalse, rethrow);

            EmitRedispatch();

            il.Mark(rethrow);
            il.Op(OpCodes.Rethrow);
        }

        // ---- operand access --------------------------------------------

        private void PushRegister(int index) => il.Load(_registerLocals[index]);

        private static void BeginSetRegister(int index) => _ = index;

        private void EndSetRegister(int index)
        {
            _localWrites.Add(index);
            il.Store(_registerLocals[index]);
        }

        // The frame's array is what the collector traces and what a frame
        // entering or leaving compiled code reads, so these are the two points
        // where the locals and the array have to agree.
        private void SpillRegister(int index)
        {
            il.Load(_registers);
            il.Int(index);
            il.LoadElementAddress(typeof(JsValue));
            il.Load(_registerLocals[index]);
            il.StoreObject(typeof(JsValue));
        }

        private void ReloadRegister(int index)
        {
            il.Load(_registers);
            il.Int(index);
            il.LoadElementAddress(typeof(JsValue));
            il.LoadObject(typeof(JsValue));
            il.Store(_registerLocals[index]);
        }

        /// <summary>
        /// Starts a branch the fast path did not take. What the fast path wrote to
        /// a local it wrote on the other side of the branch, so it says nothing
        /// about what this one has to read back.
        /// </summary>
        private void BeginColdBranch() => _localWrites.Clear();

        private void SpillLiveIn(int ip)
        {
            for (var r = 0; r < _registerLocals.Length; r++)
            {
                if (_liveness.IsLiveIn(ip, r)) SpillRegister(r);
            }
        }

        private void ReloadLiveIn(int ip)
        {
            for (var r = 0; r < _registerLocals.Length; r++)
            {
                if (_liveness.IsLiveIn(ip, r)) ReloadRegister(r);
            }
        }

        /// <summary>
        /// Reads back the registers a call may have left in the frame: those it
        /// can write, that something still reads, and that this opcode has not
        /// already put in a local itself.
        /// </summary>
        private void ReloadLiveOut(int ip)
        {
            for (var r = 0; r < _registerLocals.Length; r++)
            {
                if (_liveness.IsLiveOut(ip, r) &&
                    _liveness.IsWrittenBack(ip, r) &&
                    !_localWrites.Contains(r))
                {
                    ReloadRegister(r);
                }
            }
        }

        /// <summary>
        /// Whether an opcode's compiled form always leaves the method. The ones
        /// that answer false either touch nothing but locals and labels, or keep
        /// their call on a branch that spills for itself.
        /// </summary>
        private static bool IsSafepoint(OpCode op) => op switch
        {
            OpCode.Nop or OpCode.LoadConst or OpCode.Move or OpCode.Jump or
            OpCode.JumpIfFalse or OpCode.Return or OpCode.LoadNewTarget or
            OpCode.PushHandler or OpCode.PopHandler or
            OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.Div or OpCode.Mod or OpCode.Exp or
            OpCode.Eq or OpCode.Neq or OpCode.StrictEq or OpCode.StrictNeq or
            OpCode.Lt or OpCode.Gt or OpCode.Le or OpCode.Ge or OpCode.And or OpCode.Or or
            OpCode.BitAnd or OpCode.BitOr or OpCode.BitXor or
            OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.UnsignedShiftRight or
            OpCode.Not or OpCode.Pos or OpCode.Neg or OpCode.Void or OpCode.TypeOf or
            OpCode.BitNot or OpCode.ToNumeric or OpCode.Increment or OpCode.Decrement or
            OpCode.LoadVar or OpCode.StoreVar or
            OpCode.GetPropByName or OpCode.SetPropByName => false,
            _ => true,
        };

        private void PushTag(LocalBuilder value)
        {
            il.LoadAddress(value);
            il.LoadField(FiValueTag);
        }

        private void PushPooled(LocalBuilder pool, int index)
        {
            il.Load(pool);
            il.Int(index);
            il.LoadElementRef();
        }

        private int PoolPropertySite(string name, CacheIRSite site)
        {
            _names.Add(name);
            _sites.Add(site);
            return _names.Count - 1;
        }

        // ---- opcodes ---------------------------------------------------

        private bool TryEmit(Instruction ins, int ip)
        {
            var registerCount = function.RegisterCount;
            bool Register(int index) => (uint)index < (uint)registerCount;
            bool Target(int index) => (uint)index < (uint)instructions.Length;

            switch (ins.OpCode)
            {
                case OpCode.Nop:
                    return true;

                case OpCode.LoadConst:
                    if (!Register(ins.A) || (uint)ins.B >= (uint)function.Constants.Count) return false;
                    BeginSetRegister(ins.A);
                    il.Load(_constants);
                    il.Int(ins.B);
                    il.LoadElementAddress(typeof(JsValue));
                    il.LoadObject(typeof(JsValue));
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.Move:
                    if (!Register(ins.A) || !Register(ins.B)) return false;
                    BeginSetRegister(ins.A);
                    PushRegister(ins.B);
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.LoadVar:
                    return TryEmitLoadVar(ins, ip, Register);

                case OpCode.StoreVar:
                    return TryEmitStoreVar(ins, ip, Register);

                case OpCode.InitVar:
                    if (!Register(ins.A)) return false;
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.B);
                    PushRegister(ins.A);
                    il.Call(MiInitializeName);
                    return true;

                case OpCode.PreResolveVar:
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.B);
                    il.Call(MiPreResolveBinding);
                    return true;

                case OpCode.StoreResolvedVar:
                    if (!Register(ins.A)) return false;
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.B);
                    PushRegister(ins.A);
                    il.Call(MiStoreToResolvedBinding);
                    return true;

                case OpCode.Return:
                    if (!Register(ins.A)) return false;
                    PushRegister(ins.A);
                    il.Store(_returnValue);
                    il.Branch(OpCodes.Leave, _exit);
                    return true;

                // A strict `return f(...)`. Operands as for Call0/1/N; the frame
                // is replaced rather than a result stored.
                case OpCode.TailCall0:
                    return EmitTailCall(ins.B, 0, 0);

                case OpCode.TailCall1:
                    return EmitTailCall(ins.B, ins.C, 1);

                case OpCode.TailCallN:
                    return EmitTailCall(ins.B, ins.C, ins.D);

                case OpCode.Jump:
                    if (!Target(ins.A)) return false;
                    il.Branch(OpCodes.Br, _labels[ins.A]);
                    return true;

                case OpCode.JumpIfFalse:
                    if (!Register(ins.A) || !Target(ins.B)) return false;
                    il.Arg(0);
                    PushRegister(ins.A);
                    il.Call(MiIsTruthy);
                    il.Branch(OpCodes.Brfalse, _labels[ins.B]);
                    return true;

                case OpCode.LoadThis:
                    if (!Register(ins.A)) return false;
                    BeginSetRegister(ins.A);
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ip);
                    il.Call(MiLoadThis);
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.LoadNewTarget:
                    if (!Register(ins.A)) return false;
                    BeginSetRegister(ins.A);
                    il.Arg(1);
                    il.Get(PiNewTarget);
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.NewObject:
                    if (!Register(ins.A)) return false;
                    BeginSetRegister(ins.A);
                    il.Arg(0);
                    il.Call(MiNewObject);
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.NewArray:
                    if (!Register(ins.A) || ins.B < 0) return false;
                    BeginSetRegister(ins.A);
                    il.Arg(0);
                    il.Int(ins.B);
                    il.Call(MiNewArray);
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.InitThisBinding:
                    il.Arg(0);
                    il.Arg(1);
                    il.Call(MiInitThisBinding);
                    return true;

                case OpCode.EnterScope:
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.A);
                    il.Int(ins.B);
                    il.Int(ins.D);
                    il.Call(MiEnterScope);
                    // The frame stands on a different environment now, so the
                    // slot storage hoisted at entry no longer describes it.
                    EmitRefreshSlots();
                    return true;

                case OpCode.LeaveScope:
                    il.Arg(0);
                    il.Arg(1);
                    il.Call(MiLeaveScope);
                    EmitRefreshSlots();
                    return true;

                case OpCode.NextIterationEnv:
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.A);
                    il.Call(MiCreatePerIterationEnvironment);
                    EmitRefreshSlots();
                    return true;

                case OpCode.EndFinally:
                    return TryEmitEndFinally();

                case OpCode.TypeOfName:
                    if (!Register(ins.A)) return false;
                    BeginSetRegister(ins.A);
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.B);
                    il.Call(MiTypeOfName);
                    il.Call(MiJsValueFromString);
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.CreateFunction:
                    if (!Register(ins.A) || (uint)ins.B >= (uint)function.NestedFunctions.Count) return false;
                    BeginSetRegister(ins.A);
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.B);
                    il.Call(MiCreateFunctionFromNested);
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.NewRegExp:
                    if (!Register(ins.A) || (uint)ins.B >= (uint)function.Constants.Count) return false;
                    BeginSetRegister(ins.A);
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.B);
                    il.Call(MiNewRegExp);
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.Throw:
                    if (!Register(ins.A)) return false;
                    // Raised for real rather than asking the interpreter to move
                    // an instruction pointer this body does not have. The catch
                    // routes it to a handler here or lets it leave.
                    PushRegister(ins.A);
                    il.New(CtorJsThrown);
                    il.Op(OpCodes.Throw);
                    return true;

                case OpCode.PushHandler:
                    il.Arg(1);
                    il.Get(PiCatchHandlers);
                    il.Int(ins.A);
                    il.Call(MiIntStackPush);
                    il.Arg(1);
                    il.Get(PiFinallyHandlers);
                    il.Int(ins.D);
                    il.Call(MiIntStackPush);
                    il.Arg(1);
                    il.Get(PiHandlerEnvironments);
                    il.Arg(1);
                    il.Get(PiFrameEnvironment);
                    il.Call(MiEnvStackPush);
                    return true;

                case OpCode.PopHandler:
                    return TryEmitPopHandler();

                case OpCode.GetPropByName:
                {
                    if (!Register(ins.A) || !Register(ins.B)) return false;
                    if ((uint)ins.C >= (uint)function.PropertyNames.Count) return false;
                    EmitCachedLoad(
                        PoolPropertySite(
                            function.PropertyNames[ins.C],
                            function.EnsureLoadCacheSite(ip)),
                        ins.A,
                        ins.B,
                        ip);
                    return true;
                }

                case OpCode.SetPropByName:
                {
                    if (!Register(ins.A) || !Register(ins.C)) return false;
                    if ((uint)ins.B >= (uint)function.PropertyNames.Count) return false;
                    // D=1 is an object literal's create, not an assignment, and
                    // the store cache below performs an assignment - which would
                    // run an inherited setter the specification says must not
                    // run. It goes through the dispatch loop's define instead.
                    if (ins.D != 0)
                    {
                        il.Arg(0);
                        il.Arg(1);
                        PushRegister(ins.A);
                        il.Int(ins.B);
                        PushRegister(ins.C);
                        il.Call(MiDefineLiteralProperty);
                        return true;
                    }
                    var name = function.PropertyNames[ins.B];
                    EmitCachedStore(
                        PoolPropertySite(name, function.EnsureStoreCacheSite(ip)),
                        ins.A,
                        ins.C,
                        ip,
                        // The only key whose store carries bookkeeping beyond the
                        // write, and it is known here rather than compared at run
                        // time.
                        marksPrototype: string.Equals(name, "prototype", StringComparison.Ordinal));
                    return true;
                }

                case OpCode.DeletePropByName:
                    if (!Register(ins.A) || !Register(ins.B)) return false;
                    if ((uint)ins.C >= (uint)function.PropertyNames.Count) return false;
                    return EmitVoidCall(MiDeletePropByName, ins.A, ins.B, ins.C);

                case OpCode.GetElem:
                    if (!Register(ins.A) || !Register(ins.B) || !Register(ins.C)) return false;
                    return EmitVoidCall(MiGetElem, ins.A, ins.B, ins.C, ip);

                case OpCode.GetElemConst:
                    if (!Register(ins.A) || !Register(ins.B)) return false;
                    if ((uint)ins.C >= (uint)function.Constants.Count) return false;
                    return EmitVoidCall(MiGetElemConst, ins.A, ins.B, ins.C, ip);

                case OpCode.SetElem:
                    if (!Register(ins.A) || !Register(ins.B) || !Register(ins.C)) return false;
                    return EmitVoidCall(MiSetElem, ins.A, ins.B, ins.C);

                case OpCode.SetElemByIndex:
                    if (!Register(ins.A) || ins.B < 0 || !Register(ins.C)) return false;
                    return EmitVoidCall(MiSetElemByIndex, ins.A, ins.B, ins.C);

                case OpCode.DeleteElem:
                    if (!Register(ins.A) || !Register(ins.B) || !Register(ins.C)) return false;
                    return EmitVoidCall(MiDeleteElem, ins.A, ins.B, ins.C);

                case OpCode.Call0:
                    return EmitVoidCall(MiCall0, ins.A, ins.B, ins.E, ip);

                case OpCode.Call1:
                    return EmitVoidCall(MiCall1, ins.A, ins.B, ins.C, ins.E, ip);

                case OpCode.CallN:
                    if (ins.D < 0) return false;
                    return EmitVoidCall(MiCallN, ins.A, ins.B, ins.C, ins.D, ins.E, ip);

                case OpCode.CallMethod0:
                    return EmitVoidCall(MiCallMethod0, ins.A, ins.B, ins.C, ip);

                case OpCode.CallMethod1:
                    return EmitVoidCall(MiCallMethod1, ins.A, ins.B, ins.C, ins.D, ip);

                case OpCode.CallMethodN:
                    if (ins.E < 0) return false;
                    return EmitVoidCall(MiCallMethodN, ins.A, ins.B, ins.C, ins.D, ins.E, ip);

                case OpCode.CallSpread:
                    return EmitVoidCall(MiCallSpread, ins.A, ins.B, ins.C, ins.D);

                case OpCode.Construct0:
                    return EmitVoidCall(MiConstruct0, ins.A, ins.B);

                case OpCode.Construct1:
                    return EmitVoidCall(MiConstruct1, ins.A, ins.B, ins.C);

                case OpCode.ConstructN:
                    if (ins.D < 0) return false;
                    return EmitVoidCall(MiConstructN, ins.A, ins.B, ins.C, ins.D);

                case OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.Div or OpCode.Mod or OpCode.Exp or
                     OpCode.Eq or OpCode.Neq or OpCode.StrictEq or OpCode.StrictNeq or
                     OpCode.Lt or OpCode.Gt or OpCode.Le or OpCode.Ge or OpCode.And or OpCode.Or or
                     OpCode.BitAnd or OpCode.BitOr or OpCode.BitXor or
                     OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.UnsignedShiftRight:
                    if (!Register(ins.A) || !Register(ins.B) || !Register(ins.C)) return false;
                    EmitBinary(ins, ip);
                    return true;

                case OpCode.Not or OpCode.Pos or OpCode.Neg or OpCode.Void or OpCode.TypeOf or
                     OpCode.BitNot or OpCode.ToNumeric or OpCode.Increment or OpCode.Decrement:
                    if (!Register(ins.A) || !Register(ins.B)) return false;
                    EmitUnary(ins, ip);
                    return true;

                case OpCode.In:
                    if (!Register(ins.A) || !Register(ins.B) || !Register(ins.C)) return false;
                    return EmitVoidCall(MiInOp, ins.A, ins.B, ins.C);

                case OpCode.InstanceOf:
                    if (!Register(ins.A) || !Register(ins.B) || !Register(ins.C)) return false;
                    return EmitVoidCall(MiInstanceOfOp, ins.A, ins.B, ins.C);

                case OpCode.Delete:
                    if (!Register(ins.A)) return false;
                    return EmitVoidCall(MiDeleteOp, ins.A, ins.B);

                case OpCode.SetPrototype:
                    if (!Register(ins.A) || !Register(ins.B)) return false;
                    return EmitVoidCall(MiSetPrototypeOp, ins.A, ins.B);

                case OpCode.EnumerateKeys:
                    if (!Register(ins.A) || !Register(ins.B)) return false;
                    BeginSetRegister(ins.A);
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.B);
                    il.Call(MiEnumerateKeys);
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.EnumerateValues:
                    if (!Register(ins.A) || !Register(ins.B)) return false;
                    BeginSetRegister(ins.A);
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.B);
                    il.Call(MiEnumerateValues);
                    EndSetRegister(ins.A);
                    return true;

                case OpCode.ForOfNext:
                    if (!Register(ins.A) || !Register(ins.B) || !Target(ins.C)) return false;
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.A);
                    il.Int(ins.B);
                    il.Call(MiForOfNext);
                    il.Branch(OpCodes.Brtrue, _labels[ins.C]);
                    return true;

                case OpCode.ForInNext:
                    if (!Register(ins.A) || !Register(ins.B) || !Target(ins.C)) return false;
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.A);
                    il.Int(ins.B);
                    il.Call(MiForInNext);
                    il.Branch(OpCodes.Brtrue, _labels[ins.C]);
                    return true;

                case OpCode.IteratorClose:
                    if (!Register(ins.B)) return false;
                    il.Arg(0);
                    il.Arg(1);
                    il.Int(ins.B);
                    il.Call(MiIteratorClose);
                    return true;

                case OpCode.DefinePrivateField:
                    if (!Register(ins.A) || !Register(ins.C)) return false;
                    if ((uint)ins.B >= (uint)function.PropertyNames.Count) return false;
                    return EmitVoidCall(MiDefinePrivateField, ins.A, ins.B, ins.C);

                case OpCode.GetPrivateField:
                    if (!Register(ins.A) || !Register(ins.B)) return false;
                    if ((uint)ins.C >= (uint)function.PropertyNames.Count) return false;
                    return EmitVoidCall(MiGetPrivateField, ins.A, ins.B, ins.C);

                case OpCode.SetPrivateField:
                    if (!Register(ins.A) || !Register(ins.C)) return false;
                    if ((uint)ins.B >= (uint)function.PropertyNames.Count) return false;
                    return EmitVoidCall(MiSetPrivateField, ins.A, ins.B, ins.C);

                case OpCode.DefineGetter or OpCode.DefineSetter:
                    il.Arg(0);
                    il.Arg(1);
                    il.Arg(1);
                    il.Get(PiFunction);
                    EmitInstruction(ins);
                    il.Call(MiHandleDefineAccessor);
                    return true;

                case OpCode.DefineGetterByReg or OpCode.DefineSetterByReg:
                    il.Arg(0);
                    il.Arg(1);
                    EmitInstruction(ins);
                    il.Call(MiHandleDefineAccessorByReg);
                    return true;

                case OpCode.SetHomeObject:
                    il.Arg(0);
                    il.Arg(1);
                    EmitInstruction(ins);
                    il.Call(MiHandleSetHomeObject);
                    return true;

                case OpCode.LoadSuperProperty:
                    il.Arg(0);
                    il.Arg(1);
                    il.Arg(1);
                    il.Get(PiFunction);
                    EmitInstruction(ins);
                    il.Call(MiHandleLoadSuperProperty);
                    return true;

                case OpCode.LoadSuperElement:
                    il.Arg(0);
                    il.Arg(1);
                    EmitInstruction(ins);
                    il.Call(MiHandleLoadSuperElement);
                    return true;

                case OpCode.LoadSuperConstructor:
                    il.Arg(0);
                    il.Arg(1);
                    EmitInstruction(ins);
                    il.Call(MiHandleLoadSuperConstructor);
                    return true;

                default:
                    return false;
            }
        }

        // The dispatch loop records the call and returns, and ExecuteInternal
        // makes it once this frame is gone, which is what keeps a tail-recursive
        // function from growing the stack. Compiled code does the same.
        private bool EmitTailCall(int callee, int argStart, int argCount)
        {
            var registerCount = (uint)function.RegisterCount;
            if ((uint)callee >= registerCount || argCount < 0) return false;
            if (argCount > 0 && ((uint)argStart >= registerCount || (uint)(argStart + argCount - 1) >= registerCount)) return false;

            EmitVoidCall(MiRequestTailCall, callee, argStart, argCount);
            il.Get(PiJsValueUndefined);
            il.Store(_returnValue);
            il.Branch(OpCodes.Leave, _exit);
            return true;
        }

        private bool EmitVoidCall(MethodInfo target, params ReadOnlySpan<int> operands)
        {
            il.Arg(0);
            il.Arg(1);
            foreach (var operand in operands) il.Int(operand);
            il.Call(target);
            return true;
        }

        /// <summary>
        /// A cached property read, with the cache program's guards written out
        /// here rather than performed by something this calls. Every guard fails
        /// closed to the same miss helper the interpreter uses, so a shape the
        /// program does not cover, a stale slot, an accessor and a proxy all
        /// leave by the one path where the specification's ordering still holds.
        /// </summary>
        private void EmitCachedLoad(int pooled, int dest, int receiver, int ip)
        {
            var miss = il.DefineLabel();
            var done = il.DefineLabel();

            PushRegister(receiver);
            il.Store(_lhs);
            il.Arg(0);
            il.Load(_lhs);
            il.Call(MiCacheableLoadReceiver);
            il.Store(_receiver);
            il.Load(_receiver);
            il.Branch(OpCodes.Brfalse, miss);

            EmitGuardShape(pooled, PiInlineLoadShape, miss);

            il.Load(_receiver);
            il.Load(_program);
            il.Get(PiResultSlot);
            il.LoadAddress(_cached);
            il.Call(MiTryReadDataSlot);
            il.Branch(OpCodes.Brfalse, miss);

            BeginSetRegister(dest);
            il.Load(_cached);
            EndSetRegister(dest);
            il.Branch(OpCodes.Br, done);

            il.Mark(miss);
            BeginColdBranch();
            SpillLiveIn(ip);
            il.Arg(0);
            il.Arg(1);
            il.Int(dest);
            il.Int(receiver);
            PushPooled(_namePool, pooled);
            PushPooled(_sitePool, pooled);
            il.Call(MiLoadPropertyMiss);
            ReloadLiveOut(ip);
            il.Mark(done);
        }

        /// <summary>The write half, guarded the same way.</summary>
        private void EmitCachedStore(int pooled, int receiver, int value, int ip, bool marksPrototype)
        {
            var miss = il.DefineLabel();
            var done = il.DefineLabel();
            var slot = il.Local(typeof(int));

            PushRegister(receiver);
            il.Store(_lhs);
            il.Arg(0);
            il.Load(_lhs);
            il.Call(MiCacheableStoreReceiver);
            il.Store(_receiver);
            il.Load(_receiver);
            il.Branch(OpCodes.Brfalse, miss);

            EmitGuardShape(pooled, PiInlineStoreShape, miss);

            il.Load(_program);
            il.Get(PiResultSlot);
            il.Store(slot);
            il.Load(_receiver);
            il.Load(slot);
            il.Call(MiIsWritableDataSlot);
            il.Branch(OpCodes.Brfalse, miss);

            PushRegister(value);
            il.Store(_rhs);
            il.Load(_receiver);
            il.Load(slot);
            il.Load(_rhs);
            il.Call(MiWriteDataSlot);
            il.Arg(0);
            il.Load(_lhs);
            il.Load(_rhs);
            il.Call(MiCachedStoreBarrier);

            if (marksPrototype)
            {
                il.Arg(0);
                il.Load(_receiver);
                PushPooled(_namePool, pooled);
                il.Load(_rhs);
                il.Call(MiMarkPrototypeAssignment);
            }

            il.Branch(OpCodes.Br, done);

            il.Mark(miss);
            BeginColdBranch();
            SpillLiveIn(ip);
            il.Arg(0);
            il.Arg(1);
            il.Int(receiver);
            PushPooled(_namePool, pooled);
            il.Int(value);
            PushPooled(_sitePool, pooled);
            il.Call(MiStorePropertyMiss);
            ReloadLiveOut(ip);
            il.Mark(done);
        }

        /// <summary>
        /// Takes the site's first program and branches to <paramref name="miss"/>
        /// unless it guards the receiver's current shape. A program that has gone
        /// stale reports no shape, so it leaves by the same branch.
        /// </summary>
        private void EmitGuardShape(int pooled, PropertyInfo inlineShape, Label miss)
        {
            PushPooled(_sitePool, pooled);
            il.Get(PiSiteFirst);
            il.Store(_program);
            il.Load(_program);
            il.Branch(OpCodes.Brfalse, miss);

            il.Load(_program);
            il.Get(inlineShape);
            il.Store(_shape);
            il.Load(_shape);
            il.Branch(OpCodes.Brfalse, miss);

            il.Load(_receiver);
            il.Get(PiObjectShape);
            il.Load(_shape);
            il.Branch(OpCodes.Bne_Un, miss);
        }

        private void EmitInstruction(Instruction ins)
        {
            il.Int((int)ins.OpCode);
            il.Int(ins.A);
            il.Int(ins.B);
            il.Int(ins.C);
            il.Int(ins.D);
            il.Int(ins.E);
            il.New(CtorInstruction);
        }

        private bool TryEmitPopHandler()
        {
            var done = il.DefineLabel();
            var noEnvironment = il.DefineLabel();

            il.Arg(1);
            il.Get(PiCatchHandlers);
            il.Get(PiIntStackCount);
            il.Branch(OpCodes.Brfalse, done);

            il.Arg(1);
            il.Get(PiCatchHandlers);
            il.Call(MiIntStackPop);
            il.Op(OpCodes.Pop);
            il.Arg(1);
            il.Get(PiFinallyHandlers);
            il.Call(MiIntStackPop);
            il.Op(OpCodes.Pop);

            il.Arg(1);
            il.Get(PiHandlerEnvironments);
            il.Get(PiEnvStackCount);
            il.Branch(OpCodes.Brfalse, noEnvironment);
            il.Arg(1);
            il.Get(PiHandlerEnvironments);
            il.Call(MiEnvStackPop);
            il.Op(OpCodes.Pop);
            il.Mark(noEnvironment);

            il.Mark(done);
            return true;
        }

        private bool TryEmitEndFinally()
        {
            // Re-dispatching needs the entry chain; without one the branch would
            // land on the first instruction instead of the handler.
            if (!_routable) return false;

            var action = il.Local(typeof(int));
            var pending = il.Local(typeof(JsValue));
            var notReturning = il.DefineLabel();
            var done = il.DefineLabel();

            il.Arg(0);
            il.Arg(1);
            il.LoadAddress(pending);
            il.Call(MiEndFinally);
            il.Store(action);

            il.Load(action);
            il.Int(2);
            il.Branch(OpCodes.Bne_Un, notReturning);
            il.Load(pending);
            il.Store(_returnValue);
            il.Branch(OpCodes.Leave, _exit);

            il.Mark(notReturning);
            il.Load(action);
            il.Int(1);
            il.Branch(OpCodes.Bne_Un, done);
            EmitRedispatch();
            il.Mark(done);
            return true;
        }

        // ---- variable slots --------------------------------------------

        private bool TryEmitLoadVar(Instruction ins, int ip, Func<int, bool> register)
        {
            if (!register(ins.A) || ins.B < 0) return false;

            var slow = il.DefineLabel();
            var done = il.DefineLabel();

            EmitSlotGuard(ins.B, slow);

            BeginSetRegister(ins.A);
            il.Load(_slot);
            il.Get(PiBindingValue);
            EndSetRegister(ins.A);
            il.Branch(OpCodes.Br, done);

            il.Mark(slow);
            BeginColdBranch();
            SpillLiveIn(ip);
            BeginSetRegister(ins.A);
            il.Arg(0);
            il.Arg(1);
            il.Int(ins.B);
            il.Int(ip);
            il.Call(MiLoadSlotFast);
            EndSetRegister(ins.A);
            il.Mark(done);
            return true;
        }

        private bool TryEmitStoreVar(Instruction ins, int ip, Func<int, bool> register)
        {
            if (!register(ins.A) || ins.B < 0) return false;

            var slow = il.DefineLabel();
            var done = il.DefineLabel();

            PushRegister(ins.A);
            il.Store(_lhs);

            EmitSlotGuard(ins.B, slow);

            // A non-object value needs no write barrier, and the barrier is the
            // only reason this had to be a call.
            PushTag(_lhs);
            il.Int((int)JsValueTag.Object);
            il.Branch(OpCodes.Beq, slow);

            il.Load(_slot);
            il.Get(PiBindingMutable);
            il.Branch(OpCodes.Brfalse, slow);

            il.Load(_slot);
            il.Load(_lhs);
            il.Load(_slot);
            il.Get(PiBindingMutable);
            il.Load(_slot);
            il.Get(PiBindingInitialized);
            il.Load(_slot);
            il.Get(PiBindingStrict);
            il.Load(_slot);
            il.Get(PiBindingDeletable);
            il.New(CiBinding);
            il.StoreObject(BindingType);
            il.Branch(OpCodes.Br, done);

            il.Mark(slow);
            BeginColdBranch();
            SpillLiveIn(ip);
            il.Arg(0);
            il.Arg(1);
            il.Int(ins.B);
            il.Load(_lhs);
            il.Int(ip);
            il.Call(MiStoreSlotFast);
            il.Mark(done);
            return true;
        }

        /// <summary>
        /// Takes the slot's address into a byref local and branches to
        /// <paramref name="slow"/> unless it holds an initialized binding. The
        /// address is the point of the exercise: every field the caller then
        /// reads or writes costs no further bounds check. An absent slot reads
        /// as a default binding, so it is uninitialized and the guard turns it
        /// away without a presence flag of its own.
        /// </summary>
        private void EmitSlotGuard(int slot, Label slow)
        {
            il.Load(_slotBindings);
            il.Branch(OpCodes.Brfalse, slow);

            il.Int(slot);
            il.Load(_slotBindings);
            il.Op(OpCodes.Ldlen);
            il.Op(OpCodes.Conv_I4);
            il.Branch(OpCodes.Bge_Un, slow);

            il.Load(_slotBindings);
            il.Int(slot);
            il.LoadElementAddress(BindingType);
            il.Store(_slot);

            il.Load(_slot);
            il.Get(PiBindingInitialized);
            il.Branch(OpCodes.Brfalse, slow);
        }

        // ---- operators -------------------------------------------------

        private void EmitBinary(Instruction ins, int ip)
        {
            if (!HasFastPath(ins.OpCode))
            {
                EmitBinarySlow(ins, ip);
                return;
            }

            var slow = il.DefineLabel();
            var done = il.DefineLabel();

            PushRegister(ins.B);
            il.Store(_lhs);
            PushRegister(ins.C);
            il.Store(_rhs);

            if (ins.OpCode is OpCode.BitAnd or OpCode.BitOr or OpCode.BitXor
                or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.UnsignedShiftRight)
            {
                GuardInt32(_lhs, slow);
                GuardInt32(_rhs, slow);
            }
            else
            {
                GuardNumeric(_lhs, slow);
                GuardNumeric(_rhs, slow);
            }

            BeginSetRegister(ins.A);
            EmitBinaryFast(ins.OpCode);
            EndSetRegister(ins.A);
            il.Branch(OpCodes.Br, done);

            il.Mark(slow);
            EmitBinarySlow(ins, ip);
            il.Mark(done);
        }

        private static bool HasFastPath(OpCode op) => op is not (OpCode.And or OpCode.Or or OpCode.Exp);

        // A general operator reads its operands out of the frame and writes its
        // result back there, so the locals and the array have to agree across it.
        private void EmitBinarySlow(Instruction ins, int ip)
        {
            BeginColdBranch();
            SpillLiveIn(ip);
            il.Arg(0);
            il.Arg(1);
            il.Int((int)ins.OpCode);
            il.Int(ins.A);
            il.Int(ins.B);
            il.Int(ins.C);
            il.Call(MiApplyBinop);
            ReloadLiveOut(ip);
        }

        private void EmitBinaryFast(OpCode op)
        {
            switch (op)
            {
                case OpCode.Add:
                    PushNumbers();
                    il.Op(OpCodes.Add);
                    il.Call(MiFastNumberResult);
                    return;
                case OpCode.Sub:
                    PushNumbers();
                    il.Op(OpCodes.Sub);
                    il.Call(MiFastNumberResult);
                    return;
                case OpCode.Mul:
                    PushNumbers();
                    il.Op(OpCodes.Mul);
                    il.Call(MiFastNumberResult);
                    return;
                case OpCode.Div:
                    PushNumbers();
                    il.Op(OpCodes.Div);
                    il.Call(MiFastNumberResult);
                    return;
                case OpCode.Mod:
                    PushNumbers();
                    il.Op(OpCodes.Rem);
                    il.Call(MiFastNumberResult);
                    return;
                case OpCode.Lt:
                    PushNumbers();
                    il.Op(OpCodes.Clt);
                    il.Call(MiFromBoolean);
                    return;
                case OpCode.Gt:
                    PushNumbers();
                    il.Op(OpCodes.Cgt);
                    il.Call(MiFromBoolean);
                    return;
                case OpCode.Le:
                    // Unordered has to answer false, so the negated form is the
                    // one that carries NaN correctly.
                    PushNumbers();
                    il.Op(OpCodes.Cgt_Un);
                    EmitNegateBool();
                    il.Call(MiFromBoolean);
                    return;
                case OpCode.Ge:
                    PushNumbers();
                    il.Op(OpCodes.Clt_Un);
                    EmitNegateBool();
                    il.Call(MiFromBoolean);
                    return;
                case OpCode.Eq or OpCode.StrictEq:
                    PushNumbers();
                    il.Op(OpCodes.Ceq);
                    il.Call(MiFromBoolean);
                    return;
                case OpCode.Neq or OpCode.StrictNeq:
                    PushNumbers();
                    il.Op(OpCodes.Ceq);
                    EmitNegateBool();
                    il.Call(MiFromBoolean);
                    return;
                case OpCode.BitAnd:
                    PushIntegers();
                    il.Op(OpCodes.And);
                    il.Call(MiFromInt32);
                    return;
                case OpCode.BitOr:
                    PushIntegers();
                    il.Op(OpCodes.Or);
                    il.Call(MiFromInt32);
                    return;
                case OpCode.BitXor:
                    PushIntegers();
                    il.Op(OpCodes.Xor);
                    il.Call(MiFromInt32);
                    return;
                case OpCode.ShiftLeft:
                    PushShift();
                    il.Op(OpCodes.Shl);
                    il.Call(MiFromInt32);
                    return;
                case OpCode.ShiftRight:
                    PushShift();
                    il.Op(OpCodes.Shr);
                    il.Call(MiFromInt32);
                    return;
                case OpCode.UnsignedShiftRight:
                    PushShift();
                    il.Op(OpCodes.Shr_Un);
                    il.Op(OpCodes.Conv_R_Un);
                    il.Call(MiFastNumberResult);
                    return;
            }
        }

        private void PushNumbers()
        {
            il.LoadAddress(_lhs);
            il.Call(MiAsNumber);
            il.LoadAddress(_rhs);
            il.Call(MiAsNumber);
        }

        private void PushIntegers()
        {
            il.LoadAddress(_lhs);
            il.Call(MiAsInt32);
            il.LoadAddress(_rhs);
            il.Call(MiAsInt32);
        }

        /// <summary>ECMA-262 13.9: a shift count is taken modulo 32.</summary>
        private void PushShift()
        {
            il.LoadAddress(_lhs);
            il.Call(MiAsInt32);
            il.LoadAddress(_rhs);
            il.Call(MiAsInt32);
            il.Int(31);
            il.Op(OpCodes.And);
        }

        private void EmitNegateBool()
        {
            il.Int(0);
            il.Op(OpCodes.Ceq);
        }

        private void EmitUnary(Instruction ins, int ip)
        {
            if (ins.OpCode is OpCode.Void or OpCode.TypeOf or OpCode.BitNot)
            {
                EmitUnarySlow(ins, ip);
                return;
            }

            var slow = il.DefineLabel();
            var done = il.DefineLabel();

            PushRegister(ins.B);
            il.Store(_lhs);

            if (ins.OpCode == OpCode.Not)
            {
                PushTag(_lhs);
                il.Int((int)JsValueTag.Boolean);
                il.Branch(OpCodes.Bne_Un, slow);
            }
            else
            {
                GuardNumeric(_lhs, slow);
            }

            BeginSetRegister(ins.A);
            EmitUnaryFast(ins.OpCode);
            EndSetRegister(ins.A);
            il.Branch(OpCodes.Br, done);

            il.Mark(slow);
            EmitUnarySlow(ins, ip);
            il.Mark(done);
        }

        private void EmitUnarySlow(Instruction ins, int ip)
        {
            BeginColdBranch();
            SpillLiveIn(ip);
            il.Arg(0);
            il.Arg(1);
            il.Int((int)ins.OpCode);
            il.Int(ins.A);
            il.Int(ins.B);
            il.Call(MiApplyUnaryOp);
            ReloadLiveOut(ip);
        }

        /// <summary>
        /// Tagging matches the dispatch loop's: an integral result keeps the
        /// Int32 tag, because the tag decides which fast paths the operators
        /// downstream can take, and a loop counter feeds the bitwise operators a
        /// minified bundle is built out of.
        /// </summary>
        private void EmitUnaryFast(OpCode op)
        {
            switch (op)
            {
                case OpCode.Increment:
                    il.LoadAddress(_lhs);
                    il.Call(MiAsNumber);
                    il.Double(1.0);
                    il.Op(OpCodes.Add);
                    il.Call(MiFastNumberResult);
                    return;
                case OpCode.Decrement:
                    il.LoadAddress(_lhs);
                    il.Call(MiAsNumber);
                    il.Double(1.0);
                    il.Op(OpCodes.Sub);
                    il.Call(MiFastNumberResult);
                    return;
                case OpCode.Neg:
                    il.LoadAddress(_lhs);
                    il.Call(MiAsNumber);
                    il.Op(OpCodes.Neg);
                    il.Call(MiFastNumberResult);
                    return;
                case OpCode.Pos:
                    il.LoadAddress(_lhs);
                    il.Call(MiAsNumber);
                    il.Call(MiFastNumberResult);
                    return;
                case OpCode.ToNumeric:
                    il.Load(_lhs);
                    return;
                case OpCode.Not:
                    il.LoadAddress(_lhs);
                    il.Call(MiAsBoolean);
                    EmitNegateBool();
                    il.Call(MiFromBoolean);
                    return;
            }
        }

        private void GuardNumeric(LocalBuilder value, Label fail)
        {
            var numeric = il.DefineLabel();
            PushTag(value);
            il.Int((int)JsValueTag.Int32);
            il.Branch(OpCodes.Beq, numeric);
            PushTag(value);
            il.Int((int)JsValueTag.Number);
            il.Branch(OpCodes.Bne_Un, fail);
            il.Mark(numeric);
        }

        private void GuardInt32(LocalBuilder value, Label fail)
        {
            PushTag(value);
            il.Int((int)JsValueTag.Int32);
            il.Branch(OpCodes.Bne_Un, fail);
        }
    }
}
#endif
