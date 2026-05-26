using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class LabeledStatementRuntimeTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBoolean(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    // ---- Labeled break from non-loop block ----

    [Fact]
    public void LabeledBreak_Block_ExitsAtBreak()
    {
        Assert.Equal(1, RunNum("var x; myLabel:{ x=1; break myLabel; x=2; } x;"));
    }

    [Fact]
    public void LabeledBreak_Block_PostBreakCodeNotExecuted()
    {
        Assert.Equal(5, RunNum("var a; exitBlock:{ a=5; break exitBlock; a=99; } a;"));
    }

    // ---- Labeled break from loops ----

    [Fact]
    public void LabeledBreak_While_ExitsOuterLoop()
    {
        Assert.Equal(3, RunNum("var i=0; outer:while(i<10){ i=i+1; if(i>=3)break outer; } i;"));
    }

    [Fact]
    public void LabeledBreak_For_ExitsOuterLoop()
    {
        // outer:for(i=0;i<10;i++){ for(j=0;j<10;j++){ if(i===3)break outer; } } → i === 3
        // The inner loop runs j=0..9, then when i===3 break outer exits. i is 3.
        Assert.Equal(3, RunNum("var i,j; outer:for(i=0;i<10;i=i+1){ for(j=0;j<10;j=j+1){ if(i===3)break outer; } } i;"));
    }

    [Fact]
    public void LabeledBreak_DoWhile_ExitsOuterLoop()
    {
        Assert.Equal(1, RunNum("var i=0; outer:do{i=i+1; if(i>=1)break outer;}while(true); i;"));
    }

    [Fact]
    public void LabeledBreak_ForIn_ExitsOuterLoop()
    {
        Assert.True(RunBoolean(@"
            var obj={a:1,b:2,c:3,d:4,e:5};
            var found=false;
            outer:for(var k in obj){ if(k==='c'){ found=true; break outer; } }
            found;
        "));
    }

    [Fact]
    public void LabeledBreak_ForOf_ExitsOuterLoop()
    {
        Assert.True(RunBoolean(@"
            var arr=[1,2,3,4,5];
            var found=false;
            outer:for(var v of arr){ if(v===3){ found=true; break outer; } }
            found;
        "));
    }

    [Fact]
    public void LabeledBreak_NestedLoops_OnlyExitsMatchingLabel()
    {
        // For each i, j=0,1 increment sum (2), then break inner → 3 * 2 = 6
        Assert.Equal(6, RunNum(@"
            var sum=0;
            outer:for(var i=0;i<3;i=i+1){
                inner:for(var j=0;j<5;j=j+1){
                    if(j===2) break inner;
                    sum=sum+1;
                }
            }
            sum;
        "));
    }

    // ---- Labeled continue ----

    [Fact]
    public void LabeledContinue_ContinuesOuterLoop()
    {
        // Inner loop: count++ for j=0,1 only (j===2 continues outer) → 2 per outer iter, 5 iters → 10
        Assert.Equal(10, RunNum(@"
            var i,j,count=0;
            outer:for(i=0;i<5;i=i+1){
                for(j=0;j<5;j=j+1){
                    if(j===2) continue outer;
                    count=count+1;
                }
            }
            count;
        "));
    }

    [Fact]
    public void LabeledContinue_While_ContinuesOuterWhile()
    {
        Assert.Equal(4, RunNum(@"
            var i=0,count=0;
            outer:while(i<5){
                if(i===0){ i=i+1; continue outer; }
                count=count+1;
                i=i+1;
            }
            count;
        "));
    }

    [Fact]
    public void LabeledContinue_DoWhile_ContinuesOuterDoWhile()
    {
        Assert.Equal(4, RunNum(@"
            var i=0,count=0;
            outer:do{
                if(i===0){ i=i+1; continue outer; }
                count=count+1;
                i=i+1;
            }while(i<5);
            count;
        "));
    }

    [Fact]
    public void LabeledContinue_ForIn_ContinuesOuterForIn()
    {
        Assert.Equal(0, RunNum(@"
            var obj={a:1,b:2,c:3,d:4};
            var count=0;
            outer:for(var k in obj){
                continue outer;
                count=count+1;
            }
            count;
        "));
    }

    // ---- Unlabeled break/continue still works ----

    [Fact]
    public void UnlabeledBreak_StillWorks()
    {
        Assert.Equal(3, RunNum("var x=0; for(;;){ x=x+1; if(x>=3)break; } x;"));
    }

    [Fact]
    public void UnlabeledContinue_StillWorks()
    {
        Assert.Equal(4, RunNum(@"
            var sum=0;
            for(var i=0;i<5;i=i+1){
                if(i===2) continue;
                sum=sum+1;
            }
            sum;
        "));
    }

    // ---- Error cases ----

    [Fact]
    public void LabeledBreak_UndefinedLabel_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RunNum("var x; myLabel:{ x=1; break wrongLabel; x=2; } x;"));
    }

    [Fact]
    public void LabeledContinue_UndefinedLabel_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RunNum("for(var i=0;i<5;i=i+1){ continue outer; }"));
    }

    [Fact]
    public void BreakWithoutLoopOrLabel_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RunNum("break;"));
    }

    [Fact]
    public void ContinueWithoutLoop_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RunNum("continue;"));
    }
}
