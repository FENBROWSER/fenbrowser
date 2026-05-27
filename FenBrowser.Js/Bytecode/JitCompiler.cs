using System.Linq.Expressions;
using System.Reflection;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Bytecode;

// Tier 4 #24 baseline JIT.
//
// Two compilation paths share one JitDelegate slot:
//
//   1. Constant-fold (abstract interpretation): for functions whose body
//      is a closed-form sequence of arithmetic/logic/control-flow on
//      compile-time-known values, TryCompile returns a delegate that
//      simply yields the folded result. Built on the existing abstract
//      interpreter — extended over multiple commits.
//
//   2. Expression-tree codegen: for functions touching runtime state
//      (LoadVar, StoreVar, InitVar so far), TryCompile builds a
//      System.Linq.Expressions tree mirroring the interpreter's switch
//      case-by-case, then Expression.Compile() turns it into a delegate.
//      Each compiled call into the interpreter's helper methods is the
//      same code the switch dispatch would execute, just bound at
//      JIT-compile time rather than resolved per-instruction.
//
// Either path can return null to fall back to the interpreter switch.
// The IL-emit path is the multi-commit effort opened by this commit; new
// opcodes are added by extending TryEmitOpcode below.
public static class JitCompiler
{
    public const int TierUpThreshold = 100;

    // JIT-compiled body. Runs to completion inside the caller-set-up
    // InterpreterFrame and returns the function's return value. Throws
    // JsThrownException for uncaught exceptions, same as ExecuteInternal.
    public delegate JsValue JitDelegate(BytecodeInterpreter interp, InterpreterFrame frame);

    public static long CompileAttempts;
    public static long CompileSuccesses;
    public static long CompileExpressionTreeSuccesses;

    public static JitDelegate? TryCompile(BytecodeFunction function)
    {
        Interlocked.Increment(ref CompileAttempts);
        if (function is null || function.Instructions.Count == 0) return null;

        // Path 1: try the cheap constant-fold first. If every register's
        // value at Return is statically known, we emit a delegate that
        // returns it directly.
        if (TryConstantFold(function) is { } folded)
        {
            Interlocked.Increment(ref CompileSuccesses);
            return folded;
        }

        // Path 2: Expression-tree codegen. Compiles a per-function
        // delegate that runs the same dispatch as ExecuteInternal but
        // with each opcode bound at JIT-compile time. Bails to null if
        // any opcode in the function lacks an emitter — the interpreter
        // takes over.
        var emitted = TryEmitExpressionTree(function);
        if (emitted is not null)
        {
            Interlocked.Increment(ref CompileSuccesses);
            Interlocked.Increment(ref CompileExpressionTreeSuccesses);
            return emitted;
        }

        return null;
    }

    // ---- Path 1: constant-fold abstract interpreter -----------------

    private static JitDelegate? TryConstantFold(BytecodeFunction function)
    {
        var registerValues = new JsValue?[function.RegisterCount];
        var ip = 0;
        var stepsRemaining = function.Instructions.Count * 4;

        while (stepsRemaining-- > 0)
        {
            if ((uint)ip >= (uint)function.Instructions.Count) return null;
            var ins = function.Instructions[ip];
            switch (ins.OpCode)
            {
                case OpCode.LoadConst:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.Constants.Count) return null;
                    registerValues[ins.A] = function.Constants[ins.B];
                    ip++;
                    break;
                case OpCode.Move:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.RegisterCount) return null;
                    if (registerValues[ins.B] is not { } srcValue) return null;
                    registerValues[ins.A] = srcValue;
                    ip++;
                    break;
                case OpCode.Add:
                case OpCode.Sub:
                case OpCode.Mul:
                case OpCode.Div:
                case OpCode.Mod:
                case OpCode.Exp:
                case OpCode.BitAnd:
                case OpCode.BitOr:
                case OpCode.BitXor:
                case OpCode.ShiftLeft:
                case OpCode.ShiftRight:
                case OpCode.UnsignedShiftRight:
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
                    if (!TryFoldBinop(function, ins, registerValues, out var foldedValue))
                        return null;
                    registerValues[ins.A] = foldedValue;
                    ip++;
                    break;
                case OpCode.Not:
                case OpCode.Pos:
                case OpCode.Neg:
                case OpCode.BitNot:
                case OpCode.Void:
                case OpCode.TypeOf:
                    if (!TryFoldUnaryOp(function, ins, registerValues, out var foldedUnary))
                        return null;
                    registerValues[ins.A] = foldedUnary;
                    ip++;
                    break;
                case OpCode.Jump:
                    if (ins.A < 0 || ins.A >= function.Instructions.Count) return null;
                    ip = ins.A;
                    break;
                case OpCode.JumpIfFalse:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.Instructions.Count) return null;
                    if (registerValues[ins.A] is not { } condValue) return null;
                    ip = IsTruthy(condValue) ? ip + 1 : ins.B;
                    break;
                case OpCode.Return:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (registerValues[ins.A] is not { } returnValue) return null;
                    return (_, _) => returnValue;
                default:
                    return null;
            }
        }
        return null;
    }

    // ---- Path 2: Expression-tree codegen ---------------------------

    private static readonly MethodInfo MiLoadName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.LoadName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiStoreName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.StoreName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiInitializeName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.InitializeName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo PiRegisters = typeof(InterpreterFrame).GetProperty(nameof(InterpreterFrame.Registers))!;
    private static readonly PropertyInfo PiFunction = typeof(InterpreterFrame).GetProperty(nameof(InterpreterFrame.Function))!;
    private static readonly PropertyInfo PiConstants = typeof(BytecodeFunction).GetProperty(nameof(BytecodeFunction.Constants))!;

    private static JitDelegate? TryEmitExpressionTree(BytecodeFunction function)
    {
        var interpParam = Expression.Parameter(typeof(BytecodeInterpreter), "interp");
        var frameParam = Expression.Parameter(typeof(InterpreterFrame), "frame");
        var registersLocal = Expression.Variable(typeof(JsValue[]), "registers");
        var constantsLocal = Expression.Variable(typeof(IReadOnlyList<JsValue>), "constants");
        var returnLabel = Expression.Label(typeof(JsValue), "return");

        var instructionLabels = new LabelTarget[function.Instructions.Count];
        for (var i = 0; i < instructionLabels.Length; i++)
            instructionLabels[i] = Expression.Label("ip_" + i);

        var body = new List<Expression>
        {
            Expression.Assign(registersLocal, Expression.Property(frameParam, PiRegisters)),
            Expression.Assign(constantsLocal, Expression.Property(Expression.Property(frameParam, PiFunction), PiConstants)),
        };

        for (var i = 0; i < function.Instructions.Count; i++)
        {
            body.Add(Expression.Label(instructionLabels[i]));
            var ins = function.Instructions[i];
            if (!TryEmitOpcode(function, ins, i, interpParam, frameParam, registersLocal, constantsLocal, instructionLabels, returnLabel, body))
            {
                return null;
            }
        }

        // If control flow falls off the end without hitting Return, the
        // function returns undefined — matches interpreter behavior.
        body.Add(Expression.Label(returnLabel, Expression.Constant(JsValue.Undefined)));

        var block = Expression.Block(typeof(JsValue), new[] { registersLocal, constantsLocal }, body);
        var lambda = Expression.Lambda<JitDelegate>(block, interpParam, frameParam);
        try { return lambda.Compile(); }
        catch { return null; }
    }

    private static bool TryEmitOpcode(
        BytecodeFunction function, Instruction ins, int ip,
        ParameterExpression interp, ParameterExpression frame,
        ParameterExpression registers, ParameterExpression constants,
        LabelTarget[] labels, LabelTarget returnLabel, List<Expression> body)
    {
        switch (ins.OpCode)
        {
            case OpCode.LoadConst:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.Constants.Count) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Property(constants, "Item", Expression.Constant(ins.B))));
                return true;
            case OpCode.Move:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.ArrayAccess(registers, Expression.Constant(ins.B))));
                return true;
            case OpCode.LoadVar:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Call(interp, MiLoadName, frame, Expression.Constant(ins.B))));
                return true;
            case OpCode.StoreVar:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiStoreName, frame, Expression.Constant(ins.B),
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A))));
                return true;
            case OpCode.InitVar:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiInitializeName, frame, Expression.Constant(ins.B),
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A))));
                return true;
            case OpCode.Return:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Return(returnLabel,
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A))));
                return true;
            case OpCode.Jump:
                if (ins.A < 0 || ins.A >= function.Instructions.Count) return false;
                body.Add(Expression.Goto(labels[ins.A]));
                return true;
            // Future opcodes added here as the IL-emit work continues.
            default:
                return false;
        }
    }

    // ---- shared helpers for the constant-fold path -----------------

    private static bool TryFoldBinop(BytecodeFunction function, Instruction ins, JsValue?[] regs, out JsValue result)
    {
        result = default;
        if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
        if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
        if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
        if (regs[ins.B] is not { } lhs || regs[ins.C] is not { } rhs) return false;

        switch (ins.OpCode)
        {
            case OpCode.Add:
                if (lhs.Tag == JsValueTag.String && rhs.Tag == JsValueTag.String)
                { result = JsValue.FromString(lhs.AsString() + rhs.AsString()); return true; }
                if (!TryGetNumber(lhs, out var addL) || !TryGetNumber(rhs, out var addR)) return false;
                result = JsValue.FromNumber(addL + addR); return true;
            case OpCode.Sub:
            case OpCode.Mul:
            case OpCode.Div:
            case OpCode.Mod:
            case OpCode.Exp:
                if (!TryGetNumber(lhs, out var nL) || !TryGetNumber(rhs, out var nR)) return false;
                result = JsValue.FromNumber(ins.OpCode switch
                {
                    OpCode.Sub => nL - nR,
                    OpCode.Mul => nL * nR,
                    OpCode.Div => nL / nR,
                    OpCode.Mod => nL % nR,
                    OpCode.Exp => Math.Pow(nL, nR),
                    _ => double.NaN,
                });
                return true;
            case OpCode.BitAnd:
            case OpCode.BitOr:
            case OpCode.BitXor:
                if (!TryGetInt32(lhs, out var iL) || !TryGetInt32(rhs, out var iR)) return false;
                result = JsValue.FromInt32(ins.OpCode switch
                {
                    OpCode.BitAnd => iL & iR,
                    OpCode.BitOr => iL | iR,
                    OpCode.BitXor => iL ^ iR,
                    _ => 0,
                });
                return true;
            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:
                if (!TryGetInt32(lhs, out var sL) || !TryGetInt32(rhs, out var sR)) return false;
                var shift = sR & 0x1F;
                result = JsValue.FromInt32(ins.OpCode == OpCode.ShiftLeft ? sL << shift : sL >> shift);
                return true;
            case OpCode.UnsignedShiftRight:
                if (!TryGetInt32(lhs, out var uL) || !TryGetInt32(rhs, out var uR)) return false;
                var ushift = uR & 0x1F;
                result = JsValue.FromNumber((double)((uint)uL >> ushift));
                return true;
            case OpCode.StrictEq:
                result = JsValue.FromBoolean(StrictEquals(lhs, rhs)); return true;
            case OpCode.StrictNeq:
                result = JsValue.FromBoolean(!StrictEquals(lhs, rhs)); return true;
            case OpCode.Eq:
            case OpCode.Neq:
                if (lhs.Tag != rhs.Tag) return false;
                var eqResult = StrictEquals(lhs, rhs);
                result = JsValue.FromBoolean(ins.OpCode == OpCode.Eq ? eqResult : !eqResult);
                return true;
            case OpCode.Lt:
            case OpCode.Gt:
            case OpCode.Le:
            case OpCode.Ge:
                if (!TryGetNumber(lhs, out var cL) || !TryGetNumber(rhs, out var cR)) return false;
                if (double.IsNaN(cL) || double.IsNaN(cR))
                { result = JsValue.FromBoolean(false); return true; }
                result = JsValue.FromBoolean(ins.OpCode switch
                {
                    OpCode.Lt => cL < cR,
                    OpCode.Gt => cL > cR,
                    OpCode.Le => cL <= cR,
                    OpCode.Ge => cL >= cR,
                    _ => false,
                });
                return true;
            case OpCode.And:
                result = IsTruthy(lhs) ? rhs : lhs; return true;
            case OpCode.Or:
                result = IsTruthy(lhs) ? lhs : rhs; return true;
        }
        return false;
    }

    private static bool TryFoldUnaryOp(BytecodeFunction function, Instruction ins, JsValue?[] regs, out JsValue result)
    {
        result = default;
        if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
        if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
        if (regs[ins.B] is not { } v) return false;

        switch (ins.OpCode)
        {
            case OpCode.Not:
                result = JsValue.FromBoolean(!IsTruthy(v)); return true;
            case OpCode.Void:
                result = JsValue.Undefined; return true;
            case OpCode.Pos:
                if (!TryGetNumber(v, out var pn)) return false;
                result = JsValue.FromNumber(pn); return true;
            case OpCode.Neg:
                if (!TryGetNumber(v, out var nn)) return false;
                result = JsValue.FromNumber(-nn); return true;
            case OpCode.BitNot:
                if (!TryGetInt32(v, out var bi)) return false;
                result = JsValue.FromInt32(~bi); return true;
            case OpCode.TypeOf:
                result = JsValue.FromString(v.Tag switch
                {
                    JsValueTag.Undefined => "undefined",
                    JsValueTag.Null => "object",
                    JsValueTag.Boolean => "boolean",
                    JsValueTag.Int32 or JsValueTag.Number => "number",
                    JsValueTag.String => "string",
                    JsValueTag.Symbol => "symbol",
                    JsValueTag.BigInt => "bigint",
                    _ => "object",
                });
                return true;
        }
        return false;
    }

    private static bool StrictEquals(JsValue a, JsValue b)
    {
        if (a.Tag != b.Tag)
        {
            if ((a.Tag == JsValueTag.Int32 || a.Tag == JsValueTag.Number) &&
                (b.Tag == JsValueTag.Int32 || b.Tag == JsValueTag.Number))
            {
                TryGetNumber(a, out var na);
                TryGetNumber(b, out var nb);
                return na == nb;
            }
            return false;
        }
        return a.Tag switch
        {
            JsValueTag.Undefined or JsValueTag.Null => true,
            JsValueTag.Boolean => a.AsBoolean() == b.AsBoolean(),
            JsValueTag.Int32 => a.AsInt32() == b.AsInt32(),
            JsValueTag.Number => a.AsNumber() == b.AsNumber(),
            JsValueTag.String => string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsTruthy(JsValue v) => v.Tag switch
    {
        JsValueTag.Undefined => false,
        JsValueTag.Null => false,
        JsValueTag.Boolean => v.AsBoolean(),
        JsValueTag.Int32 => v.AsInt32() != 0,
        JsValueTag.Number => v.AsNumber() != 0 && !double.IsNaN(v.AsNumber()),
        JsValueTag.String => v.AsString().Length > 0,
        _ => true,
    };

    private static bool TryGetNumber(JsValue v, out double n)
    {
        switch (v.Tag)
        {
            case JsValueTag.Number: n = v.AsNumber(); return true;
            case JsValueTag.Int32: n = v.AsInt32(); return true;
            case JsValueTag.Boolean: n = v.AsBoolean() ? 1 : 0; return true;
            case JsValueTag.Null: n = 0; return true;
            default: n = 0; return false;
        }
    }

    private static bool TryGetInt32(JsValue v, out int n)
    {
        switch (v.Tag)
        {
            case JsValueTag.Int32: n = v.AsInt32(); return true;
            case JsValueTag.Number:
                var d = v.AsNumber();
                if (double.IsNaN(d) || double.IsInfinity(d)) { n = 0; return true; }
                n = unchecked((int)(uint)d);
                return true;
            case JsValueTag.Boolean: n = v.AsBoolean() ? 1 : 0; return true;
            case JsValueTag.Null: n = 0; return true;
            default: n = 0; return false;
        }
    }
}
