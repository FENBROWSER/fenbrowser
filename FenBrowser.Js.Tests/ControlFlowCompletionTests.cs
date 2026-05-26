using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ControlFlowCompletionTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    // ---- do...while tests ----

    [Fact]
    public void DoWhile_BasicLoop()
    {
        // x = 0; do { x = x + 1; } while (x < 5); x == 5
        Assert.Equal(5, RunNum("var x=0; do{x=x+1;}while(x<5); x;"));
    }

    [Fact]
    public void DoWhile_ExecutesAtLeastOnce()
    {
        // do { x = 1; } while (false); x == 1
        Assert.Equal(1, RunNum("var x=0; do{x=1;}while(false); x;"));
    }

    [Fact]
    public void DoWhile_WithBreak()
    {
        // x=0; do { x=x+1; if(x>=3)break; } while(true); x==3
        Assert.Equal(3, RunNum("var x=0; do{x=x+1;if(x>=3)break;}while(true); x;"));
    }

    [Fact]
    public void DoWhile_WithContinue()
    {
        // skip even numbers: sum = 0; i=0; do{i=i+1; if(i%2==0)continue; sum=sum+i;}while(i<10);
        Assert.Equal(25, RunNum("var sum=0,i=0; do{i=i+1;if(i%2==0)continue;sum=sum+i;}while(i<10); sum;"));
    }

    // ---- Switch tests ----

    [Fact]
    public void Switch_BasicMatch()
    {
        // var r=0; switch(2){case 1:r=10;break;case 2:r=20;break;default:r=99;}
        Assert.Equal(20, RunNum("var r=0; switch(2){case 1:r=10;break;case 2:r=20;break;default:r=99;} r;"));
    }

    [Fact]
    public void Switch_DefaultCase()
    {
        // var r=0; switch(99){case 1:r=10;break;default:r=50;break;}
        Assert.Equal(50, RunNum("var r=0; switch(99){case 1:r=10;break;default:r=50;break;} r;"));
    }

    [Fact]
    public void Switch_Fallthrough()
    {
        // JS fallthrough: every case body executes until break
        Assert.Equal(3, RunNum("var r=0; switch(1){case 1:r=1;case 2:r=2;case 3:r=3;break;default:r=99;} r;"));
    }

    [Fact]
    public void Switch_NoMatchNoDefault()
    {
        Assert.Equal(0, RunNum("var r=0; switch(99){case 1:r=1;break;} r;"));
    }

    [Fact]
    public void Switch_WithBreakInCase()
    {
        // break inside switch should exit the switch, not a parent loop
        Assert.Equal(20, RunNum("var r=0; for(var i=0;i<5;i=i+1){switch(i){case 0:r=10;break;case 2:r=20;break;default:r=r;}} r;"));
    }

    // ---- Labeled statement tests ----

    [Fact]
    public void Labeled_BasicStatement()
    {
        // myLabel: { var x = 42; } x == 42
        Assert.Equal(42, RunNum("myLabel:{var x=42;} x;"));
    }

    [Fact]
    public void Labeled_WithExpression()
    {
        // label with expression statement
        Assert.Equal(99, RunNum("var r=0; outer: r=99; r;"));
    }

    // ---- Block scoping tests ----

    [Fact]
    public void BlockScope_LetInBlockIsAccessible()
    {
        // let inside block should be accessible within the block
        Assert.Equal(42, RunNum("var r=0;{let x=42;r=x;} r;"));
    }

    [Fact]
    public void BlockScope_ConstInBlockIsAccessible()
    {
        // const inside block should be accessible within the block
        Assert.Equal(99, RunNum("var r=0;{const y=99;r=y;} r;"));
    }

    [Fact]
    public void BlockScope_VarOutsideBlockSurvives()
    {
        // var outside block should survive EnterScope/LeaveScope
        Assert.Equal(1, RunNum("var r=1;{let temp=2;} r;"));
    }
}
