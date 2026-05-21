using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Fuzz;

// Plan §27 bytecode fuzz target.
//
// Property: the BytecodeVerifier must reject every malformed function with a
// structured InvalidOperationException (or pass when the random function happens to
// be well-formed). It must never throw an unhandled exception like
// IndexOutOfRangeException or NullReferenceException, and it must never hang.
//
// Verifier rejection is the engine's last line of defense against the interpreter
// executing instructions with out-of-range register indices, constant indices, jump
// targets, etc. If the verifier crashes on a random input we have a hole in the
// safety boundary; that is the bug this harness is designed to catch.
public sealed class BytecodeFuzz
{
    private const int IterationsPerSeed = 64;

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public void VerifierSurvivesRandomFunctions(int seed)
    {
        var random = new Random(seed);
        var verifier = new BytecodeVerifier();
        for (var iteration = 0; iteration < IterationsPerSeed; iteration++)
        {
            var function = GenerateRandomFunction(random);
            TryVerify(verifier, function);
        }
    }

    [Fact]
    public void VerifierRejectsEmptyInstructionList()
    {
        var function = new BytecodeFunction
        {
            Name = "empty",
            Instructions = Array.Empty<Instruction>(),
            Constants = Array.Empty<JsValue>(),
            VariableSlots = new Dictionary<string, int>(),
            PropertyNames = Array.Empty<string>(),
            ParameterNames = Array.Empty<string>(),
            NestedFunctions = Array.Empty<BytecodeFunction>(),
            RegisterCount = 1,
        };

        Assert.Throws<InvalidOperationException>(() => new BytecodeVerifier().Verify(function));
    }

    [Fact]
    public void VerifierRejectsZeroRegisterFunction()
    {
        var function = new BytecodeFunction
        {
            Name = "zero",
            Instructions = new[] { new Instruction(OpCode.Return) },
            Constants = Array.Empty<JsValue>(),
            VariableSlots = new Dictionary<string, int>(),
            PropertyNames = Array.Empty<string>(),
            ParameterNames = Array.Empty<string>(),
            NestedFunctions = Array.Empty<BytecodeFunction>(),
            RegisterCount = 0,
        };

        Assert.Throws<InvalidOperationException>(() => new BytecodeVerifier().Verify(function));
    }

    private static void TryVerify(BytecodeVerifier verifier, BytecodeFunction function)
    {
        try
        {
            verifier.Verify(function);
        }
        catch (InvalidOperationException)
        {
            // Expected structured rejection.
        }
    }

    private static BytecodeFunction GenerateRandomFunction(Random random)
    {
        var registerCount = random.Next(1, 8);
        var constantCount = random.Next(0, 5);
        var instructionCount = random.Next(1, 16);

        var constants = new JsValue[constantCount];
        for (var i = 0; i < constantCount; i++)
        {
            constants[i] = JsValue.FromInt32(random.Next(-100, 100));
        }

        var instructions = new Instruction[instructionCount];
        for (var i = 0; i < instructionCount - 1; i++)
        {
            instructions[i] = RandomInstruction(random, registerCount, constantCount);
        }

        // Half the time, omit the trailing Return so the verifier exercises its
        // terminator check; the other half, include it to drive the success path
        // through the random operand checks.
        instructions[instructionCount - 1] = random.Next(0, 2) == 0
            ? new Instruction(OpCode.Return, random.Next(0, registerCount))
            : RandomInstruction(random, registerCount, constantCount);

        return new BytecodeFunction
        {
            Name = "fuzz",
            Instructions = instructions,
            Constants = constants,
            VariableSlots = new Dictionary<string, int>(),
            PropertyNames = Array.Empty<string>(),
            ParameterNames = Array.Empty<string>(),
            NestedFunctions = Array.Empty<BytecodeFunction>(),
            RegisterCount = registerCount,
        };
    }

    private static Instruction RandomInstruction(Random random, int registerCount, int constantCount)
    {
        var opcodes = Enum.GetValues<OpCode>();
        var op = opcodes[random.Next(opcodes.Length)];

        // 25% chance to inject explicitly-invalid operands so the verifier's
        // out-of-range checks see exercise.
        var injectInvalid = random.Next(0, 4) == 0;

        var a = injectInvalid ? random.Next(-3, registerCount + 5) : random.Next(0, registerCount);
        var b = op == OpCode.LoadConst
            ? (injectInvalid ? random.Next(-3, constantCount + 5) : random.Next(0, Math.Max(1, constantCount)))
            : (injectInvalid ? random.Next(-3, registerCount + 5) : random.Next(0, registerCount));
        var c = injectInvalid ? random.Next(-3, registerCount + 5) : random.Next(0, registerCount);

        return new Instruction(op, a, b, c);
    }
}
