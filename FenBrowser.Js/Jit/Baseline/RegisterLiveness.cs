using FenBrowser.Js.Bytecode;

#if !PUBLISH_AOT
namespace FenBrowser.Js.Jit.Baseline;

/// <summary>
/// Which registers hold a value that is still going to be read, at every point
/// in a function.
/// </summary>
/// <remarks>
/// Compiled code keeps registers in CLR locals, and the collector finds live
/// objects by tracing the frame's register array. Before anything that can
/// collect, the registers that are still live have to be written back, and this
/// says which those are. Getting that set too large only costs stores; getting
/// it too small loses an object, so every approximation here is made in the
/// direction of keeping a register live: an opcode whose operand roles are not
/// modelled contributes uses and no definitions.
/// </remarks>
internal sealed class RegisterLiveness
{
    private readonly int _registerCount;
    private readonly int _words;
    private readonly ulong[] _liveIn;
    private readonly ulong[] _liveOut;

    // What an instruction may leave in the frame's register array. Definitions
    // drive liveness and are kept narrow, because treating a register as written
    // when it is not would let a live value go unspilled. Write-back drives the
    // reload after a call and is kept wide for the same reason in reverse: a
    // register a helper wrote and we failed to read back leaves a stale local.
    private readonly ulong[] _writeBack;

    private RegisterLiveness(int instructionCount, int registerCount)
    {
        _registerCount = registerCount;
        _words = (registerCount + 63) / 64;
        _liveIn = new ulong[instructionCount * _words];
        _liveOut = new ulong[instructionCount * _words];
        _writeBack = new ulong[instructionCount * _words];
    }

    internal bool IsLiveIn(int ip, int register) => Test(_liveIn, ip, register);

    internal bool IsLiveOut(int ip, int register) => Test(_liveOut, ip, register);

    /// <summary>Whether an instruction may leave a register in the frame's array.</summary>
    internal bool IsWrittenBack(int ip, int register) => Test(_writeBack, ip, register);

    private bool Test(ulong[] sets, int ip, int register) =>
        (uint)register < (uint)_registerCount &&
        (sets[(ip * _words) + (register >> 6)] & (1UL << (register & 63))) != 0;

    internal static RegisterLiveness Compute(
        Instruction[] instructions, int registerCount, IReadOnlyCollection<int> handlerTargets)
    {
        var liveness = new RegisterLiveness(instructions.Length, registerCount);
        liveness.Solve(instructions, handlerTargets);
        return liveness;
    }

    private void Solve(Instruction[] instructions, IReadOnlyCollection<int> handlerTargets)
    {
        var count = instructions.Length;
        var uses = new ulong[count * _words];
        var defs = new ulong[count * _words];
        for (var ip = 0; ip < count; ip++)
        {
            Describe(instructions[ip], ip, uses, defs);
        }

        Array.Copy(defs, _writeBack, defs.Length);
        for (var ip = 0; ip < count; ip++)
        {
            if (!IsModelled(instructions[ip].OpCode)) DescribeUnmodelledWriteBack(instructions[ip], ip);
        }

        // A throw can land on any handler in this function, so anything a
        // handler goes on to read is live wherever a throw can be raised.
        var handlerLive = new ulong[_words];
        var scratch = new ulong[_words];

        bool changed;
        do
        {
            changed = false;
            for (var ip = count - 1; ip >= 0; ip--)
            {
                Array.Clear(scratch);
                foreach (var successor in Successors(instructions, ip, count))
                {
                    Or(scratch, _liveIn, successor);
                }

                if (CanRaise(instructions[ip].OpCode))
                {
                    OrWords(scratch, handlerLive);
                }

                changed |= Store(_liveOut, ip, scratch);

                // live-in = uses ∪ (live-out \ defs)
                for (var w = 0; w < _words; w++)
                {
                    scratch[w] = uses[(ip * _words) + w] | (scratch[w] & ~defs[(ip * _words) + w]);
                }

                changed |= Store(_liveIn, ip, scratch);
            }

            Array.Clear(handlerLive);
            foreach (var target in handlerTargets)
            {
                if ((uint)target < (uint)count) Or(handlerLive, _liveIn, target);
            }
        }
        while (changed);
    }

    private void Or(ulong[] destination, ulong[] source, int ip)
    {
        for (var w = 0; w < _words; w++) destination[w] |= source[(ip * _words) + w];
    }

    private static void OrWords(ulong[] destination, ulong[] source)
    {
        for (var w = 0; w < destination.Length; w++) destination[w] |= source[w];
    }

    private bool Store(ulong[] sets, int ip, ulong[] value)
    {
        var changed = false;
        for (var w = 0; w < _words; w++)
        {
            var slot = (ip * _words) + w;
            if (sets[slot] != value[w])
            {
                sets[slot] = value[w];
                changed = true;
            }
        }

        return changed;
    }

    private static IEnumerable<int> Successors(Instruction[] instructions, int ip, int count)
    {
        var instruction = instructions[ip];
        switch (instruction.OpCode)
        {
            case OpCode.Jump:
                if ((uint)instruction.A < (uint)count) yield return instruction.A;
                yield break;

            case OpCode.Return:
                yield break;

            case OpCode.Throw:
                yield break;

            case OpCode.JumpIfFalse:
                if ((uint)instruction.B < (uint)count) yield return instruction.B;
                break;

            case OpCode.ForOfNext:
            case OpCode.ForInNext:
                if ((uint)instruction.C < (uint)count) yield return instruction.C;
                break;
        }

        if (ip + 1 < count) yield return ip + 1;
    }

    /// <summary>Whether a throw raised here can reach a handler in this function.</summary>
    private static bool CanRaise(OpCode op) => op switch
    {
        OpCode.LoadConst or OpCode.Move or OpCode.Jump or OpCode.Nop or
        OpCode.PushHandler or OpCode.PopHandler => false,
        _ => true,
    };

    private void Describe(Instruction instruction, int ip, ulong[] uses, ulong[] defs)
    {
        switch (instruction.OpCode)
        {
            case OpCode.Nop:
            case OpCode.Jump:
            case OpCode.PreResolveVar:
            case OpCode.InitThisBinding:
            case OpCode.EnterScope:
            case OpCode.LeaveScope:
            case OpCode.NextIterationEnv:
            case OpCode.EndFinally:
            case OpCode.PushHandler:
            case OpCode.PopHandler:
                return;

            case OpCode.LoadConst:
            case OpCode.LoadThis:
            case OpCode.LoadNewTarget:
            case OpCode.NewObject:
            case OpCode.NewArray:
            case OpCode.TypeOfName:
            case OpCode.CreateFunction:
            case OpCode.NewRegExp:
                Define(defs, ip, instruction.A);
                return;

            case OpCode.Move:
            case OpCode.GetPropByName:
            case OpCode.GetElemConst:
            case OpCode.DeletePropByName:
            case OpCode.EnumerateKeys:
            case OpCode.EnumerateValues:
            case OpCode.ForOfNext:
            case OpCode.ForInNext:
            case OpCode.GetPrivateField:
            case OpCode.Not:
            case OpCode.Pos:
            case OpCode.Neg:
            case OpCode.Void:
            case OpCode.TypeOf:
            case OpCode.BitNot:
            case OpCode.ToNumeric:
            case OpCode.Increment:
            case OpCode.Decrement:
                Use(uses, ip, instruction.B);
                Define(defs, ip, instruction.A);
                return;

            case OpCode.LoadVar:
                Define(defs, ip, instruction.A);
                return;

            case OpCode.StoreVar:
            case OpCode.InitVar:
            case OpCode.StoreResolvedVar:
            case OpCode.Return:
            case OpCode.JumpIfFalse:
            case OpCode.Throw:
                Use(uses, ip, instruction.A);
                return;

            case OpCode.IteratorClose:
                Use(uses, ip, instruction.B);
                return;

            case OpCode.SetPropByName:
            case OpCode.SetElemByIndex:
            case OpCode.DefinePrivateField:
            case OpCode.SetPrivateField:
                Use(uses, ip, instruction.A);
                Use(uses, ip, instruction.C);
                return;

            case OpCode.GetElem:
            case OpCode.In:
            case OpCode.InstanceOf:
            case OpCode.Add:
            case OpCode.Sub:
            case OpCode.Mul:
            case OpCode.Div:
            case OpCode.Mod:
            case OpCode.Exp:
            case OpCode.Eq:
            case OpCode.Neq:
            case OpCode.StrictEq:
            case OpCode.StrictNeq:
            case OpCode.Lt:
            case OpCode.Gt:
            case OpCode.Le:
            case OpCode.Ge:
            case OpCode.And:
            case OpCode.Or:
            case OpCode.BitAnd:
            case OpCode.BitOr:
            case OpCode.BitXor:
            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:
            case OpCode.UnsignedShiftRight:
                Use(uses, ip, instruction.B);
                Use(uses, ip, instruction.C);
                Define(defs, ip, instruction.A);
                return;

            case OpCode.Call0:
            case OpCode.Construct0:
                Use(uses, ip, instruction.B);
                Define(defs, ip, instruction.A);
                return;

            case OpCode.Call1:
            case OpCode.CallMethod0:
            case OpCode.Construct1:
                Use(uses, ip, instruction.B);
                Use(uses, ip, instruction.C);
                Define(defs, ip, instruction.A);
                return;

            case OpCode.CallMethod1:
                Use(uses, ip, instruction.B);
                Use(uses, ip, instruction.C);
                Use(uses, ip, instruction.D);
                Define(defs, ip, instruction.A);
                return;

            case OpCode.CallN:
            case OpCode.ConstructN:
                Use(uses, ip, instruction.B);
                UseRange(uses, ip, instruction.C, instruction.D);
                Define(defs, ip, instruction.A);
                return;

            case OpCode.CallMethodN:
                Use(uses, ip, instruction.B);
                Use(uses, ip, instruction.C);
                UseRange(uses, ip, instruction.D, instruction.E);
                Define(defs, ip, instruction.A);
                return;

            case OpCode.CallSpread:
                Use(uses, ip, instruction.B);
                Use(uses, ip, instruction.C);
                Use(uses, ip, instruction.D);
                Define(defs, ip, instruction.A);
                return;

            default:
                // Everything not modelled above -- element writes, deletes,
                // prototype assignment, the super and accessor forms. Their
                // operands are read and nothing is assumed written, which keeps
                // every register they touch live across them.
                Use(uses, ip, instruction.A);
                Use(uses, ip, instruction.B);
                Use(uses, ip, instruction.C);
                Use(uses, ip, instruction.D);
                Use(uses, ip, instruction.E);
                return;
        }
    }

    /// <summary>
    /// Whether <see cref="Describe"/> states this opcode's operand roles rather
    /// than falling back on reading everything and assuming nothing written.
    /// </summary>
    private static bool IsModelled(OpCode op) => op switch
    {
        OpCode.SetElem or OpCode.SetElemByIndex or OpCode.DeleteElem or
        OpCode.SetPrototype or OpCode.Delete or OpCode.SetHomeObject or
        OpCode.DefineGetter or OpCode.DefineSetter or
        OpCode.DefineGetterByReg or OpCode.DefineSetterByReg or
        OpCode.LoadSuperProperty or OpCode.LoadSuperElement or
        OpCode.LoadSuperConstructor => false,
        _ => true,
    };

    private void DescribeUnmodelledWriteBack(Instruction instruction, int ip)
    {
        Define(_writeBack, ip, instruction.A);
        Define(_writeBack, ip, instruction.B);
        Define(_writeBack, ip, instruction.C);
        Define(_writeBack, ip, instruction.D);
        Define(_writeBack, ip, instruction.E);
    }

    private void Use(ulong[] uses, int ip, int register)
    {
        if ((uint)register >= (uint)_registerCount) return;
        uses[(ip * _words) + (register >> 6)] |= 1UL << (register & 63);
    }

    private void UseRange(ulong[] uses, int ip, int first, int count)
    {
        for (var i = 0; i < count; i++) Use(uses, ip, first + i);
    }

    private void Define(ulong[] defs, int ip, int register)
    {
        if ((uint)register >= (uint)_registerCount) return;
        defs[(ip * _words) + (register >> 6)] |= 1UL << (register & 63);
    }
}
#endif
