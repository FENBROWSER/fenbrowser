using System;
using System.IO;
using System.Linq;
using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JsParserReproTests
    {
        private Parser CreateParser(string input)
        {
            var lexer = new Lexer(input);
            return new Parser(lexer);
        }

        private Parser CreateRuntimeParser(string input, bool isModule = false)
        {
            var lexer = new Lexer(input);
            return new Parser(lexer, isModule: isModule, allowRecovery: false);
        }

        private Parser CreateModuleParser(string input)
        {
            var lexer = new Lexer(input);
            return new Parser(lexer, isModule: true);
        }

        private void AssertNoErrors(Parser parser)
        {
            if (parser.Errors.Any())
            {
                throw new Exception($"Parser errors:\n{string.Join("\n", parser.Errors)}");
            }
        }

        private static string ResolveRepoFile(params string[] parts)
        {
            var probe = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(probe); i++)
            {
                var candidate = Path.Combine(new[] { probe }.Concat(parts).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                probe = Path.GetDirectoryName(probe);
            }

            throw new FileNotFoundException($"Could not locate repository file: {Path.Combine(parts)}");
        }

        [Fact]
        public void Lexer_DecodesEscapedIdentifier_Exactly()
        {
            var lexer = new Lexer("var privat\\u0065 = 1;");
            // var
            var t1 = lexer.NextToken();
            // identifier
            var t2 = lexer.NextToken();
            Assert.Equal(TokenType.Identifier, t2.Type);
            Assert.Equal("private", t2.Literal);
        }

        [Fact]
        public void Parse_StrictMode_Rejects_EscapedFutureReservedWord()
        {
            var input = "\"use strict\"; var privat\\u0065 = 1;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("strict mode reserved word 'private'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_AsyncArrowFunction_InCallExpression()
        {
            // async (x) => 1
            var input = "call(async (x) => 1)";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
            
            // Validate structure
            var stmt = program.Statements.First() as ExpressionStatement;
            var call = stmt.Expression as CallExpression;
            var arrow = call.Arguments.First() as ArrowFunctionExpression;
            Assert.NotNull(arrow);
            Assert.True(arrow.IsAsync);
            Assert.Single(arrow.Parameters);
            Assert.Equal("x", arrow.Parameters[0].Value);
        }

        [Fact]
        public void Parse_AsyncArrowFunction_TwoParams()
        {
            var input = "call(async (x, y) => x + y)";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_AsyncArrowFunction_NoParams()
        {
            var input = "call(async () => 1)";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_GroupedArrowIife_BundleHeaderShape_NoErrors()
        {
            var input = "((t,e)=>{\"object\"==typeof exports&&\"object\"==typeof module?module.exports=e():\"function\"==typeof define&&define.amd?define([],e):\"object\"==typeof exports?exports.ClipboardJS=e():t.ClipboardJS=e()})(this,function(){})";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ConditionalCommaExpression_WithAsyncArrowIife_NoErrors()
        {
            var input = "var x={available:function(){if(void 0===window.WIMB.data.client_hints_frontend_available)return void 0!==navigator.userAgentData?(window.WIMB.data.client_hints_uadata=navigator.userAgentData,window.WIMB.data.client_hints_frontend_available=!0,(async()=>{var e=await navigator.userAgentData.getHighEntropyValues([\"architecture\"]);window.WIMB.data.client_hints_frontend_architecture=e.architecture})(),window.WIMB.data.client_hints_frontend_brands=window.WIMB.data.client_hints_uadata.brands,!0):window.WIMB.data.client_hints_frontend_available=!1}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_WebpackArrowBody_WithChainedAssignment_NoErrors()
        {
            var input = "var t=new Promise(((r,t)=>n=e[a]=[r,t]));";
            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_GoogleAnonymousClassSchedulerSnippet_DoesNotDesyncIntoOrphanedCatch()
        {
            var input = """
_.Qn(_.nLa,new class{
By(a){jdb(a);return _.sr.By({callback:a.play,jqa:a})}
C9a(a){jdb(a);return _.sr.By({callback:a.play,jqa:a,priority:3})}
flush(){throw Error("ke");}
Eoa(a){return _.sr.By(a)}
eDa(a,b){let c=!1;return(...d)=>{c||(c=!0,_.sr.By(()=>void(c=!1)),a.apply(b,d))}}
setTimeout(a,b,...c){return _.sr.setTimeout(a,b,...c)}
clearTimeout(a){_.sr.clearTimeout(a)}
clearInterval(a){_.sr.clearInterval(a)}
setInterval(a,b,...c){return _.sr.setInterval(a,b,...c)}
yield(){return _.wbb()}
requestIdleCallback(a,
b){return _.ubb(a,b)}
cancelIdleCallback(a){_.vbb(a)}
});
try{
  _.fB=function(a){return _.t(a,3)};
}catch(e){_._DumpException(e)}
""";

            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 2);
        }

        [Fact]
        public void Parse_GoogleClassAndTaggedTemplateSegment_RuntimeParser_NoErrors()
        {
            var input = """
HEa=class{constructor(a,b,c,d,e){this.Ba=a;this.wa=b;this.oa=c;this.ka=d;this.Aa=e;this.changes=[]}LH(a){var b=document.implementation.createHTMLDocument("");a=_.GEa(this,a,b);b=b.body;b.appendChild(a);b=(new XMLSerializer).serializeToString(b);b=b.slice(b.indexOf(">")+1,b.lastIndexOf("</"));return _.y(b)}};
_.aja=new HEa(CEa);
_.pja=new HEa(DEa);
_.oja=new HEa(EEa);
var KEa;
_.IEa=function(a){const b=new Map(a.ka.Ba);b.set("style",{qK:4});a.ka=new _.xEa(a.ka.wa,a.ka.ka,a.ka.Aa,b,a.ka.oa);return a};
_.JEa=function(a){const b=new Set(a.ka.Aa);b.add("class");a.ka=new _.xEa(a.ka.wa,a.ka.ka,b,a.ka.Ba,a.ka.oa);return a};
KEa=class{constructor(){this.oa=!1;this.ka=CEa}};
_.LEa=class extends KEa{build(){if(this.oa)throw Error("V");this.oa=!0;return new HEa(this.ka,void 0,void 0,this.Aa,this.wa)}};
var eja=/[^#]*/;
var jja={0:1,1:1},kja={0:.1,1:.1},qja;
(0,_.kc)`mica-`;
hc("lWTJwd","lwKaud");
Ju=(0,_.nk)`https://www.gstatic.com/images/icons/material/anim/mspin/mspin_googcolor_small.css`,Iu=(0,_.nk)`https://www.gstatic.com/images/icons/material/anim/mspin/mspin_googcolor_medium.css`;
""";

            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 1);
        }

        [Fact]
        public void Parse_GoogleStyleClassMethodChain_WithTryCatchTail_RuntimeParser_NoErrors()
        {
            var input = """
_.Fs=class extends _.C{
constructor(a,b){super();this.j=a;this.v=b}
Wh(a){let b;qs(this,a==null?void 0:(b=a.data)==null?void 0:b.wt)||(this.be=!0)}
nf(a){this.Y=a.data.cus?2:1}
Hf(a){var b=a.data;b=b&&b.icb?4:1;var c=this.v===5?(a=(a=a.data)&&((c=a.ctx)==null?void 0:c.eap))?!(a==="cac"||a==="aac"||a==="soac"):!0:!0;c&&ms(this,!1,b);_.ks(this,!1)}
Ug(a){var b=a.data.pid,c=a.data.ai,d=a.data.ac,e=/^\d+$/.test(b)?parseInt(b,10):-1;b=/^\d+$/.test(c)?parseInt(c,10):-1;c=/^\d+$/.test(d)?parseInt(d,10):-1;return[e,b,c]}
};
try{_.fB=function(a){return _.t(a,3)}}catch(e){_._DumpException(e)}
""";

            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 2);
        }

        [Fact]
        public void Parse_FunctionClosingAfterIfBlock_FollowedByClassExpression_NoErrors()
        {
            var input = """
ir=function(a,b){if(a){var c=1}};
nr=class extends hr{constructor(){super();this.D=[];this.style={mode:"x"}}reset(){this.i=0}};
Or=function(a,b){const [c,d]=["k","v"];return a+b+c+d};
""";

            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 3);
        }

        [Fact]
        public void Parse_MinifiedTryCatchBoundary_FollowedByTopLevelTry_RuntimeParser_NoErrors()
        {
            var input = """
var x=function(a,b,c,d,e){let f;for(let l=0;f=c[l];l++){if(!f)break;}};
try{_.fB=function(a){return _.t(a,3)}}catch(e){_._DumpException(e)}
try{
var gj=function(a){return a},hj=function(a,b){return a+b};
}catch(e){_._DumpException(e)}
""";

            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 3);
        }

        [Fact]
        public void Parse_MinifiedArrowCallbackAndCommaChain_BeforeTry_RuntimeParser_NoErrors()
        {
            var input = """
var Fu=function(a,b){
  b=b.querySelectorAll("button");
  return _.D(window,"focusout",()=>{ns(a.i,!1);ws(a.i)}),a.j.appendChild(b),b.focus(),Bs(a.i,"314px")
};
try{
  var gj=function(a){return a};
}catch(e){_._DumpException(e)}
""";

            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 2);
        }

        [Fact]
        public void Parse_GooglePageshowReasonsLabelAndForOfArrowChain_RuntimeParser_NoErrors()
        {
            var input = """
_.ze(_.yi(),"pageshow",a=>{
  var b=a.Ef;
  a=(a=Ula())&&a.type||"null";
  if((_.Qa.iV()||_.Nd())&&(a==="back_forward"||b.persisted))a:{
    var c;
    a=(c=Ula())==null?void 0:c.notRestoredReasons;
    if(a!==void 0||b.persisted)
      if(a===void 0&&b.persisted)var d="&nrrr=noAPIhit";
      else if(b.persisted)d="&nrrr=hit";
      else if(a===null)d="&nrrr=null";
      else{
        b:{c=a.reasons;if(c!==null&&c.length!==0)for(e of c)if(e.reason==="session-restored"){var e=!0;break b}e=!1}
        if(e)break a;
        var f;
        e=`&murl=${encodeURIComponent((f=a.url)!=null?f:"unknown")}`;
        f=[];
        if(a.reasons!==null&&a.reasons.length>0)for(d of a.reasons)d.reason&&f.push(d.reason);else f.push("mainNotBlocking");
        d=`&mnrr=${f.join(",")}`;
        d=`${e}${d}`;
        f=a.children!==null?Wla(a.children,""):"";
        d+=`&nrrr=${f}`
      }
      else d="&nrrr=noAPImiss";
    _.ff("nrr",d)
  }
},!1);
_.ze(_.yi(),"popstate",()=>{_.Qa.Pz()&&Yla&&Xla===_.Ld().href?(clearTimeout(Yla),Xla=Yla=null):_.Vla("popstate")},!1);
""";

            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 2);
        }

        [Fact]
        public void Parse_GoogleFocusoutHandlerThenTopLevelTry_RuntimeParser_NoErrors()
        {
            var input = """
var Eu=function(a){
  if(a.B==1&&a.C&&a.F){
    _.Ph(a.j,"background",a.i.D?"#282a2c":"#e9eef6");
    _.Nf(a.j);
    var b=new bu(a.G);
    b=b.Za();
    Iu(a,b)||a.o.H(b,"focusout",()=>{qs(a.i,!1);zs(a.i)});
    a.j.appendChild(b);b.focus();Es(a.i,"314px")
  }
},Ju=function(a){a.j&&(_.Of(a.j),a.B!=1&&_.ci(a.J,!0),a.j=null)},Iu=function(a,b){b=b.querySelectorAll("button");return b.length==1?(a.o.H(b[0],"click",()=>{_.ns(a.i,!1);zs(a.i)}),!0):!1};
try{
  var gj=function(a){return a},hj=function(a,b){return a+b};
}catch(e){_._DumpException(e)}
""";

            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 2);
        }

        [Fact]
        public void Parse_FunctionParameterDefaultArrow_RuntimeParser_NoErrors()
        {
            var input = "var f=function(a=d=>d){return a(1)};";
            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.Single(program.Statements);
        }

        [Fact]
        public void Parse_GoogleOgBundle_TryCatchBoundaryChunk_RuntimeParser_NoErrors()
        {
            var input = """
try{
  bj.prototype.Ca=function(a,b,c,d,e){let f;for(let l=0;f=dj[l];l++){a:{var g=a;var h=f;var k=!!c;if(_.Rg(g)){h=g.wc(h,k);break a}if(!g){h=[];break a}h=(g=_.dh(g))?g.wc(h,k):[]}for(g=0;k=h[g];g++){const m=k.listener;if(m.tb==b&&m.Eh==d){e?e.Ca(a,f,k.listener,c,d):_.hh(a,f,k.listener,c,d);break}}}};_.fj=function(){return _.ej.i().i};_.ej=class{constructor(){this.i=new Hh;this.i.o.log(1)}static i(){return _.Mh(_.ej)}};var gj=_.fj();_.Kd("gbar_._DumpException",function(a){gj.j.log(a)});
}catch(e){_._DumpException(e)}
try{
  var hj=function(a){(a=_.L(a.i,_.Gg,5))?(a=_.S(a,5),a=/^\d+$/.test(a)?parseInt(a,10):0):a=0;return a},ij=function(a,b){_.cj.H(a,b)};
}catch(e){_._DumpException(e)}
""";

            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 2);
        }

        [Fact]
        public void Parse_GoogleOgBundle_FocusoutChunk_RuntimeParser_NoErrors()
        {
            var input = """
var xu=class{constructor(a){this.j=a;this.i=null}Za(){var a=this.j,b=this.v,c=this.o;tu(a);b="<div"+jt("")+kt(uu(a,b,c)+"")+it(vu(a,b,c)+"")+">";c=wu(c);const d=mt(a.v);d!==""&&(b+=" <style>"+d+"</style>");b+=c+"</div>";a.j&&_.sg(a.j);a=Ip(_.oj(b));this.i&&this.i.appendChild(a);return a}fill(a,b){this.v=a;this.o=b}instantiate(a){this.i=a;this.j.i[0]="rtl"==oo(a)}},vu=function(){return' dir="'+Ao("ltr")+'"'},yu=function(){return""},uu=function(a,b,c){return"padding:"+Ao(an(a.i)?uo("padding",b?"12px":"3px"):b?"12px":"3px")+";"+(c?"":"display:"+Ao(an(a.i)?uo("display","inline-block"):"inline-block")+";")+"vertical-align:"+Ao(an(a.i)?uo("vertical-align","middle"):"middle")+";"+(c&&!an(a.i)?"margin-left:"+Ao("calc(50% - 24px)")+";":"")+(c&&an(a.i)?"margin-right:"+Ao("calc(50% - 24px)")+";":"")+(c?"margin-top:"+Ao(an(a.i)?uo("margin-top","98px"):"98px")+";":"")},zu=function(){return!0},Au=function(){return!1},Bu=function(a,b){tu(a);return wu(b.Je)},Cu=function(a,b){tu(a);var c=b.Sg;b=b.Je;a="<div"+jt("")+kt(uu(a,c,b)+"")+it(vu(a,c,b)+"")+">";c=wu(b);return _.oj(a+(c+"</div>"))},wu=function(a){return" <div"+jt((a?"mspin-medium":"mspin-small")+" ")+kt("")+it("")+"> <div> <div></div> </div> </div> "},tu=function(a){Du in a.o||ot(a,Du,{Sg:0,Je:1},Bu,Cu,zu,Au,"",vu,"",yu,uu)},Du="t-s91B_Xq1PdE";var Fu=function(a){a.o.H(a.i,"sorp",a.N);a.o.H(a.i,"sort",a.fa);a.o.H(a.i,"rav",a.Z);a.o.H(a.i,"h",a.W);a.o.H(a.i,"sdm",()=>{a.j&&a.j.querySelector("[data-fb]")&&Eu(a)});a.o.H(a.D,"sdn",a.da);a.o.H(a.D,"close",a.Y)},Eu=function(a){if(a.B==1&&a.C&&a.F){_.Ph(a.j,"background",a.i.D?"#282a2c":"#e9eef6");_.Nf(a.j);var b=new bu(a.G);Xr(a.i)?Gu(a,b,a.F):Hu(a,b,a.F);b=b.Za();var c=b.querySelectorAll("a")[0];_.mk(c,a.C);a.o.H(c,"click",()=>{var d=a.i;_.Ie(d.j,21)!=null&&gs(d,_.R(d.j,21))});Iu(a,b)||a.o.H(b,"focusout",()=>{qs(a.i,!1);zs(a.i)});a.j.appendChild(b);b.focus();Es(a.i,"314px")}},Ju=function(a){a.j&&(_.Of(a.j),a.B!=1&&_.ci(a.J,!0),a.j=null)},Iu=function(a,b){b=b.querySelectorAll("button");return b.length==1?(a.o.H(b[0],"click",()=>{_.ns(a.i,!1);zs(a.i)}),!0):!1};
""";

            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 1);
        }

        [Fact]
        public void Parse_GoogleOgBundle_ClassAndTooltipTryBoundary_RuntimeParser_NoErrors()
        {
            var input = """
try{
E.prototype.Lc=function(a){var b=this.I();b&&_.Of(b);E.U.Lc.call(this,a);a?(b=this.B.i.body,b.insertBefore(a,b.lastChild),_.Fc(this.o),this.o=new _.id(this.I()),_.uf(this,this.o),_.D(this.o,"focusin",this.C,void 0,this),_.D(this.o,"focusout",this.M,void 0,this)):(_.Fc(this.o),this.o=null)};var Ti=function(a){return a.j?a.isVisible()?4:1:a.D?3:a.isVisible()?2:0};
E.prototype.rd=function(){if(!sd.prototype.rd.call(this))return!1;if(this.anchor){var a;for(let b=0;a=Ri[b];b++)_.Qf(a.I(),this.anchor)||a.Oa(!1)}_.ya(Ri,this)||Ri.push(this);a=this.I();a.className=this.className;this.C();_.D(a,"mouseover",this.da,!1,this);_.D(a,"mouseout",this.Z,!1,this);Ui(this);return!0};
E.prototype.vd=function(){_.za(Ri,this);const a=this.I();let b;for(let c=0;b=Ri[c];c++)b.anchor&&_.Qf(a,b.anchor)&&b.Oa(!1);this.ga&&this.ga.M();_.hh(a,"mouseover",this.da,!1,this);_.hh(a,"mouseout",this.Z,!1,this);this.anchor=void 0;Ti(this)==0&&(this.L=!1);sd.prototype.vd.call(this)};
E.prototype.la=function(a,b){this.anchor==a&&this.A.has(this.anchor)&&(this.L||!this.Qa?(this.Oa(!1),this.isVisible()||(this.anchor=a,this.R=b||this.O(0)||void 0,this.isVisible()&&this.Mb(),this.Oa(!0))):this.anchor=void 0);this.j=void 0};E.prototype.Ga=function(a){this.D=void 0;if(a==this.anchor){const b=this.B;a=(a=_.Rf(b.i))&&this.I()&&b.vf(this.I(),a);this.i!=null&&(this.i==this.I()||this.A.has(this.i))||a||this.T&&this.T.i||this.Oa(!1)}};
var Vi=function(a,b){const c=Hf(a.B.i);a.N.x=b.clientX+c.x;a.N.y=b.clientY+c.y};E.prototype.W=function(a){const b=Wi(this,a.target);this.i=b;this.C();b!=this.anchor&&(this.anchor=b,this.j||(this.j=_.Ki((0,_.G)(this.la,this,b,void 0),500)),Xi(this),Vi(this,a))};var Wi=function(a,b){try{for(;b&&!a.A.has(b);)b=b.parentNode;return b}catch(c){return null}};E.prototype.Y=function(a){Vi(this,a);this.L=!0};
E.prototype.P=function(a){this.i=a=Wi(this,a.target);this.L=!0;if(this.anchor!=a){this.anchor=a;const b=this.O(1);this.C();this.j||(this.j=_.Ki((0,_.G)(this.la,this,a,b),500));Xi(this)}};E.prototype.O=function(a){return a==0?new Yi(xf(this.N)):new Zi(this.i)};var Xi=function(a){if(a.anchor){let b;for(let c=0;b=Ri[c];c++)_.Qf(b.I(),a.anchor)&&(b.T=a,a.ga=b)}};
E.prototype.J=function(a){const b=Wi(this,a.target),c=Wi(this,a.relatedTarget);b!=c&&(b==this.i&&(this.i=null),Ui(this),this.L=!1,!this.isVisible()||a.relatedTarget&&_.Qf(this.I(),a.relatedTarget)?this.anchor=void 0:this.M())};E.prototype.da=function(){const a=this.I();this.i!=a&&(this.C(),this.i=a)};E.prototype.Z=function(a){const b=this.I();this.i!=b||a.relatedTarget&&_.Qf(b,a.relatedTarget)||(this.i=null,this.M())};var Ui=function(a){a.j&&(_.t.clearTimeout(a.j),a.j=void 0)};
E.prototype.M=function(){Ti(this)==2&&(this.D=_.Ki((0,_.G)(this.Ga,this,this.anchor),this.fa))};E.prototype.C=function(){this.D&&(_.t.clearTimeout(this.D),this.D=void 0)};E.prototype.S=function(){this.Oa(!1);Ui(this);Si(this);this.I()&&_.Of(this.I());this.i=null;delete this.B;E.U.S.call(this)};var Yi=function(a,b){ld.call(this,a,b)};_.H(Yi,ld);
Yi.prototype.i=function(a,b,c){b=Zh((a?_.Af(a):document).documentElement);c=c?new _.dd(c.top+10,c.right,c.bottom,c.left+10):new _.dd(10,0,0,10);ji(this.j,a,8,c,b,9)&496&&ji(this.j,a,8,c,b,5)};var Zi=function(a){kd.call(this,a,5)};_.H(Zi,kd);Zi.prototype.i=function(a,b,c){const d=new _.y(10,0);_.ki(this.j,this.o,a,b,d,c,9)&496&&_.ki(this.j,4,a,1,d,c,5)};var $i;_.aj=class extends E{constructor(a,b){super(a);this.className="gb_Vc";this.Lc(b);this.oa=2;this.isVisible()&&this.Mb();this.fa=100;document.addEventListener("keydown",c=>{c.keyCode==27&&this.Oa(!1)});this.I().setAttribute("ng-non-bindable","")}O(){return new $i(this.i)}Oa(a){a||Ui(this);return super.Oa(a)}};
$i=class extends kd{constructor(a){super(a,3)}i(a,b,c){const d=new _.y(0,0),e=Ff(window);let f=0;_.ki(this.j,this.o,a,b,d,c,9,void 0,new _.dd(0,e.width-8,e.height,8))&496&&(f=_.ki(this.j,4,a,1,d,c,5));f&2&&(b=parseInt(_.Th(a,"top"),10)+this.j.getBoundingClientRect().height+12,_.Ph(a,"top",b+"px"))}};var bj,dj;bj=function(){};_.cj=new bj;dj=["click","keydown","keyup"];bj.prototype.H=function(a,b,c,d,e){const f=function(g){const h=bh(b),k=_.Pf(g.target)?g.target.getAttribute("role")||null:null;g.type!="click"||g.Ua.button!=0||_.$d&&g.ctrlKey?g.keyCode!=13&&g.keyCode!=3||g.type=="keyup"?g.keyCode!=32||k!="button"&&k!="tab"&&k!="radio"||(g.type=="keyup"&&h.call(d,g),g.preventDefault()):(g.type="keypress",h.call(d,g)):h.call(d,g)};f.tb=b;f.Eh=d;e?e.H(a,dj,f,c):_.D(a,dj,f,c)};
bj.prototype.Ca=function(a,b,c,d,e){let f;for(let l=0;f=dj[l];l++){a:{var g=a;var h=f;var k=!!c;if(_.Rg(g)){h=g.wc(h,k);break a}if(!g){h=[];break a}h=(g=_.dh(g))?g.wc(h,k):[]}for(g=0;k=h[g];g++){const m=k.listener;if(m.tb==b&&m.Eh==d){e?e.Ca(a,f,k.listener,c,d):_.hh(a,f,k.listener,c,d);break}}}};_.fj=function(){return _.ej.i().i};_.ej=class{constructor(){this.i=new Hh;this.i.o.log(1)}static i(){return _.Mh(_.ej)}};var gj=_.fj();_.Kd("gbar_._DumpException",function(a){gj.j.log(a)});
}catch(e){_._DumpException(e)}
try{
var hj=function(a){(a=_.L(a.i,_.Gg,5))?(a=_.S(a,5),a=/^\d+$/.test(a)?parseInt(a,10):0):a=0;return a},ij=function(a,b){_.cj.H(a,b)};
}catch(e){_._DumpException(e)}
""";

            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 2);
        }

        [Fact]
        public void Parse_LocalGoogleOgBundle_RuntimeParser_NoErrors_WhenArtifactPresent()
        {
            string probe = AppContext.BaseDirectory;
            string path = null;
            for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(probe); i++)
            {
                var candidate = Path.Combine(probe, "logs", "google_og_bundle.js");
                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }

                probe = Path.GetDirectoryName(probe);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var input = File.ReadAllText(path);
            var parser = CreateRuntimeParser(input);
            var program = parser.ParseProgram();
            if (parser.Errors.Any())
            {
                var lines = File.ReadAllLines(path);
                int firstFailLine = -1;
                string firstFailError = null;
                for (int i = 1; i <= lines.Length; i++)
                {
                    var prefix = string.Join("\n", lines.Take(i));
                    var prefixParser = CreateRuntimeParser(prefix);
                    prefixParser.ParseProgram();
                    if (prefixParser.Errors.Any())
                    {
                        firstFailLine = i;
                        firstFailError = prefixParser.Errors[0];
                        break;
                    }
                }

                throw new Exception(
                    "Parser errors:\n" +
                    string.Join("\n", parser.Errors) +
                    $"\n\nFirstFailLine={firstFailLine}\nFirstFailError={firstFailError}");
            }

            Assert.True(program.Statements.Count >= 1);
        }

        [Fact]
        public void Parse_LocalGoogleOgBundle_MainTrySegment_RuntimeParser_NoErrors_WhenArtifactPresent()
        {
            string probe = AppContext.BaseDirectory;
            string path = null;
            for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(probe); i++)
            {
                var candidate = Path.Combine(probe, "logs", "google_og_bundle.js");
                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }

                probe = Path.GetDirectoryName(probe);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length < 521)
            {
                return;
            }

            // Keep a complete `try { ... } catch(e) { ... }` segment so the slice is syntactically valid.
            var slice = string.Join("\n", lines.Skip(310).Take(210));
            var parser = CreateRuntimeParser(slice);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            Assert.True(program.Statements.Count >= 1);
        }

        [Fact]
        public void Parse_ForLoop_CommaSequenceBody_NoErrors()
        {
            var input = "for(var e,t,n,i=\"\",r=this.array(),o=0;o<15;)e=r[o++],t=r[o++],n=r[o++],i+=BASE64_ENCODE_CHAR[e>>2]+BASE64_ENCODE_CHAR[(3&e)<<4|t>>4]+BASE64_ENCODE_CHAR[(15&t)<<2|n>>6]+BASE64_ENCODE_CHAR[63&n];";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModePromiseBranch_MinifiedSnippet_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){return new Promise(function(e){function t(){e(!0)}function n(){e(!1)}var o,i;function r(){var e=navigator.userAgent.match(/Version\\/[0-9\\._]+.*Safari/);if(e){if(parseInt(e[1],10)<11){try{localStorage.length||(localStorage.setItem(\"inPrivate\",\"0\"),localStorage.removeItem(\"inPrivate\")),n()}catch(e){(navigator.cookieEnabled?t:n)()}return!0;return}try{window.openDatabase(null,null,null,null),n()}catch(e){t()}}return e}if(((o=/(?=.*(opera|chrome)).*/i.test(navigator.userAgent)&&navigator.storage&&navigator.storage.estimate)&&navigator.storage.estimate().then(function(e){return(e.quota<12e7?t:n)()}),!o)&&((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()})}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModePromiseCallbackBody_NoErrors()
        {
            var input = "var x=function(){return new Promise(function(e){function t(){e(!0)}function n(){e(!1)}var o,i;function r(){var e=navigator.userAgent.match(/Version\\/[0-9\\._]+.*Safari/);if(e){if(parseInt(e[1],10)<11){try{localStorage.length||(localStorage.setItem(\"inPrivate\",\"0\"),localStorage.removeItem(\"inPrivate\")),n()}catch(e){(navigator.cookieEnabled?t:n)()}return!0;return}try{window.openDatabase(null,null,null,null),n()}catch(e){t()}}return e}if(((o=/(?=.*(opera|chrome)).*/i.test(navigator.userAgent)&&navigator.storage&&navigator.storage.estimate)&&navigator.storage.estimate().then(function(e){return(e.quota<12e7?t:n)()}),!o)&&((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()})};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_VarInitializerObjectFollowedByVar_MinifiedSnippet_NoErrors()
        {
            var input = "var WIMB_UTIL=(window.WIMB.init(),window.WIMB.meta.js_src_url=document.currentScript.src,WIMB_UTIL||{version:\"1.0\",get_style:function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv},decode_java_version:function(e){if(!e)return!1;var t=e.split(\".\"),n=[],o=\"\";for(v=\"1\"==t[0]&&t[1]<=\"4\"?0:1;v<t.length;v++)!1!==t[v].search(/^[0-9]+[_]+[0-9]*$/)?(fragment_fragments=t[v].split(\"_\"),n.push(parseInt(fragment_fragments[0])),void 0!==fragment_fragments[1]&&(o=parseInt(fragment_fragments[1]))):(n.push(t[v]),v++);return{version:n,update:o}}});var WIMB_CAPABILITIES=WIMB_CAPABILITIES||{capabilities:{},add:function(e,t,o){o?(WIMB_CAPABILITIES.capabilities[o]||(WIMB_CAPABILITIES.capabilities[o]={}),WIMB_CAPABILITIES.capabilities[o][e]=t):WIMB_CAPABILITIES.capabilities[e]=t}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ParenthesizedConditionalResult_Call_NoErrors()
        {
            var input = "var x = function(e){ return (e.quota < 12e7 ? t : n)(); };";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_TernaryParenthesizedCommaBranch_WithLogicalAndAssignment_NoErrors()
        {
            var input = "var x = flag ? (fragment_fragments=t[v].split(\"_\"),n.push(parseInt(fragment_fragments[0])),void 0!==fragment_fragments[1]&&(o=parseInt(fragment_fragments[1]))) : (n.push(t[v]),v++);";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_NestedSingleArgumentCall_FollowedByCommaInGroup_NoErrors()
        {
            var input = "var x = (n.push(parseInt(fragment_fragments[0])), void 0);";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModeTailGroupedCondition_NoErrors()
        {
            var input = "var x=function(){if(((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_NewPromiseCallback_MinifiedReturnTail_NoErrors()
        {
            var input = "var x=function(){return new Promise(function(e){function t(){e(!0)}function n(){e(!1)}if(((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()})};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModeHeadCondition_NoErrors()
        {
            var input = "var x=function(){if(((o=/(?=.*(opera|chrome)).*/i.test(navigator.userAgent)&&navigator.storage&&navigator.storage.estimate)&&navigator.storage.estimate().then(function(e){return(e.quota<12e7?t:n)()}),!o))return n()};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModeSafariHelper_NoErrors()
        {
            var input = "var x=function(){function r(){var e=navigator.userAgent.match(/Version\\/[0-9\\._]+.*Safari/);if(e){if(parseInt(e[1],10)<11){try{localStorage.length||(localStorage.setItem(\"inPrivate\",\"0\"),localStorage.removeItem(\"inPrivate\")),n()}catch(e){(navigator.cookieEnabled?t:n)()}return!0;return}try{window.openDatabase(null,null,null,null),n()}catch(e){t()}}return e}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModeCombinedCallbackBody_NoErrors()
        {
            var input = "var x=function(){function t(){e(!0)}function n(){e(!1)}var o,i;function r(){var e=navigator.userAgent.match(/Version\\/[0-9\\._]+.*Safari/);if(e){if(parseInt(e[1],10)<11){try{localStorage.length||(localStorage.setItem(\"inPrivate\",\"0\"),localStorage.removeItem(\"inPrivate\")),n()}catch(e){(navigator.cookieEnabled?t:n)()}return!0;return}try{window.openDatabase(null,null,null,null),n()}catch(e){t()}}return e}if(((o=/(?=.*(opera|chrome)).*/i.test(navigator.userAgent)&&navigator.storage&&navigator.storage.estimate)&&navigator.storage.estimate().then(function(e){return(e.quota<12e7?t:n)()}),!o)&&((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_WptTestdriverJs_RuntimeParser_NoErrors()
        {
            var path = ResolveRepoFile("wpt", "resources", "testdriver.js");
            var source = File.ReadAllText(path);
            var parser = CreateRuntimeParser(source);
            _ = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_WptTestdriverActionsJs_RuntimeParser_NoErrors()
        {
            var path = ResolveRepoFile("wpt", "resources", "testdriver-actions.js");
            var source = File.ReadAllText(path);
            var parser = CreateRuntimeParser(source);
            _ = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectMethodAsyncDefaultParams_NoErrors()
        {
            var source = """
const driver = {
  async click(target = { node: null }, options = { x: 0, y: 0 }) {
    return { ok: !!target, ...options };
  }
};
""";
            var parser = CreateRuntimeParser(source);
            _ = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralNestedTrailingCommaAndComments_NoErrors()
        {
            var source = """
const cfg = {
  a: {
    b: 1,
    c: 2, // trailing
  },
  d: {
    e: { f: 3, },
  },
};
""";
            var parser = CreateRuntimeParser(source);
            _ = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_OptionalChainNullishDefaultArgsInMethod_NoErrors()
        {
            var source = """
const obj = {
  run(input = {}) {
    const value = input?.meta?.value ?? "fallback";
    return value;
  },
};
""";
            var parser = CreateRuntimeParser(source);
            _ = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModeWrappedObjectLiteral_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){function t(){e(!0)}function n(){e(!1)}var o,i;function r(){var e=navigator.userAgent.match(/Version\\/[0-9\\._]+.*Safari/);if(e){if(parseInt(e[1],10)<11){try{localStorage.length||(localStorage.setItem(\"inPrivate\",\"0\"),localStorage.removeItem(\"inPrivate\")),n()}catch(e){(navigator.cookieEnabled?t:n)()}return!0;return}try{window.openDatabase(null,null,null,null),n()}catch(e){t()}}return e}if(((o=/(?=.*(opera|chrome)).*/i.test(navigator.userAgent)&&navigator.storage&&navigator.storage.estimate)&&navigator.storage.estimate().then(function(e){return(e.quota<12e7?t:n)()}),!o)&&((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralFunctionEndingWithIfReturn_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){if(cond)return n()}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralFunctionDeclarationsThenIfReturn_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){function t(){e(!0)}function n(){e(!1)}if(cond)return n()}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralFunctionReturningPromiseIfReturn_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){return new Promise(function(e){if(cond)return n()})}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralFunctionReturningPromiseWithDeclarations_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){return new Promise(function(e){function t(){e(!0)}function n(){e(!1)}if(cond)return n()})}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralFunction_ReturnsCommaTernaryComma_NoErrors()
        {
            var input = "var x={get_style:function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv},decode_java_version:function(e){return e}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_Function_ReturnsCommaTernaryComma_NoErrors()
        {
            var input = "var x=function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteral_GetStyleAndDecodeVersion_Minified_NoErrors()
        {
            var input = "var x={version:\"1.0\",get_style:function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv},decode_java_version:function(e){if(!e)return!1;var t=e.split(\".\"),n=[],o=\"\";for(v=\"1\"==t[0]&&t[1]<=\"4\"?0:1;v<t.length;v++)!1!==t[v].search(/^[0-9]+[_]+[0-9]*$/)?(fragment_fragments=t[v].split(\"_\"),n.push(parseInt(fragment_fragments[0])),void 0!==fragment_fragments[1]&&(o=parseInt(fragment_fragments[1]))):(n.push(t[v]),v++);return{version:n,update:o}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_GroupedInitializer_WithLogicalOrObjectLiteral_NoErrors()
        {
            var input = "var WIMB_UTIL=(window.WIMB.init(),window.WIMB.meta.js_src_url=document.currentScript.src,WIMB_UTIL||{version:\"1.0\",get_style:function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv},decode_java_version:function(e){return e}});";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_GroupedInitializer_WithLogicalOrObjectLiteral_SingleFunctionProperty_NoErrors()
        {
            var input = "var WIMB_UTIL=(window.WIMB.init(),window.WIMB.meta.js_src_url=document.currentScript.src,WIMB_UTIL||{get_style:function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv}});";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ComplexRegex_InConditional()
        {
            // The failing regex pattern from log
            // Note: C# verbatim string, \\u is passed as \u to Lexer if not double escaped? 
            // C# @"..." preserves backslashes. 
            // Code has \uD800. In C# string literal this is unicode char if not verbatim?
            // @"..." ignores escape sequences. So \uD800 is literally \ u D 8 0 0.
            // But Lexer expects valid JS string.
            // If Input has literally \ u D 8 0 0, JS string/regex parser handles it.
            var input = @"if(b&&(eaa?!a.isWellFormed():/(?:[^\uD800-\uDBFF]|^)[\uDC00-\uDFFF]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])/.test(a)))throw Error('q');";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_SwitchCase_DefaultReturn_AfterObjectSpreadReturn_NoErrors()
        {
            var input = "function f(e,o,t){switch(e){case\"tile\":return{...o,content:M(t)};default:return}}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_NestedSwitch_DefaultReturnDelete_ThenOuterDefault_NoErrors()
        {
            var input = "function f(e,s,m){switch(e){case 0:switch(s){case 1:return delete l[m];default:i(17,s)}default:i(17,s)}}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteral_WithArrowValuedProperties_NoErrors()
        {
            var input = "n.d(t,{v:()=>k,Z:()=>({value:1})});";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_Throw_After_Conditional()
        {
           // Context around failure
           var input = @"if(cond) throw Error('q'); a=(faa||1)";
           var parser = CreateParser(input);
           var program = parser.ParseProgram();
           AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidHexEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\xG1`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidUnicodeCodePointEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\u{110000}`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidLegacyOctalEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\08`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }
        [Fact]
        public void Parse_StrictMode_WithStatement_ShouldFail()
        {
            var parser = CreateParser("\"use strict\"; with ({ x: 1 }) { x; }");
            parser.ParseProgram();
            Assert.Contains(parser.Errors, e => e.Contains("with statement", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidUnicodeEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\u00G0`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_TaggedTemplate_AfterParenthesizedExpression_NoErrors()
        {
            var parser = CreateParser("var out = (0, tag)``;");
            var program = parser.ParseProgram();

            AssertNoErrors(parser);

            var statement = Assert.IsType<LetStatement>(program.Statements.Single());
            var tagged = Assert.IsType<TaggedTemplateExpression>(statement.Value);
            Assert.Single(tagged.Strings);
            Assert.Equal(string.Empty, tagged.Strings[0]);
        }

        [Fact]
        public void Parse_TemplateLiteral_AfterIifeReturningObjectLiteral_NoErrors()
        {
            var parser = CreateParser("var out = `${(() => ({ value: /x/.test(input) ? ok : fallback }))()}`;");
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_TemplateLiteral_WithRegexSourceInTemplateMiddle_NoErrors()
        {
            var parser = CreateParser("var out = new RegExp(`${o.source}${/\\/([\\s\\S]+)/.source}`);");
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_FunctionDeclaration_ReturningArray_ThenVar_NoErrors()
        {
            var input = "function collect(){return [a[Z][l(Gn)],a[Z][d(Sn)],a[Z][d(du)],a[Z][l(Ba)]]}var x,C,T,I;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_FunctionExpression_WithNestedFunction_ReturnArray_ThenVar_NoErrors()
        {
            var input = "O=function(){function e(){this.T=new Ui}return [a[Z][l(Gn)],a[Z][d(Sn)],a[Z][d(du)],a[Z][l(Ba)]]};var x,C,T,I;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ModuleObject_NumericFunctionProperty_UseStrict_NoErrors()
        {
            var input = "var mods={8258:function(e,t,r){\"use strict\";return void 0===l?\"\":l}};var x=1;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_FunctionK_CommaIifeCondition_NoErrors()
        {
            var input = "function k(e,t,r,n,i,o){var a=[];if(function(e,t,r,n){e[s(cR)](t,r,n)}(e,t,r,n),a[Z]=(a[se]=l(Xi),a[de]=l(Xi),function(e,t,r,n,i){if(e)try{return e[s(ER)](t,r,n,i)[s(y_)]}catch(e){return}}(e,i,o,a[se],a[de])),a[Z])return[a[Z][l(Gn)],a[Z][d(Sn)],a[Z][d(du)],a[Z][l(Ba)]]}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_FunctionK_FollowedByVarDeclaration_NoErrors()
        {
            var input = "function k(e,t,r,n,i,o){var a=[];if(function(e,t,r,n){e[s(cR)](t,r,n)}(e,t,r,n),a[Z]=(a[se]=l(Xi),a[de]=l(Xi),function(e,t,r,n,i){if(e)try{return e[s(ER)](t,r,n,i)[s(y_)]}catch(e){return}}(e,i,o,a[se],a[de])),a[Z])return[a[Z][l(Gn)],a[Z][d(Sn)],a[Z][d(du)],a[Z][l(Ba)]]}var x,C,T,I,O=function(){function e(){this.T=new Uint16Array(16),this.q=new Uint16Array(288)}};";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_RegexLiteralStartingWithEquals_InCallChain_NoErrors()
        {
            var input = "var out=r[se][u(Bo)](/\\+/g,Iw)[s(Ow)](/\\//g,wy)[Pw](/=+$/,ce);";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_ReactHelper_TryFinally_WithNestedIfElseAndDoWhile_NoErrors()
        {
            var input = "function U(e,t){if(!e||j)return\"\";j=!0;var r=Error.prepareStackTrace;Error.prepareStackTrace=void 0;try{if(t)if(t=function(){throw Error()},Object.defineProperty(t.prototype,\"props\",{set:function(){throw Error()}}),\"object\"==typeof Reflect&&Reflect.construct){try{Reflect.construct(t,[])}catch(e){var n=e}Reflect.construct(e,[],t)}else{try{t.call()}catch(e){n=e}e.call(t.prototype)}else{try{throw Error()}catch(e){n=e}e()}}catch(t){if(t&&n&&\"string\"==typeof t.stack){for(var i=t.stack.split(\"\\n\"),o=n.stack.split(\"\\n\"),a=i.length-1,u=o.length-1;1<=a&&0<=u&&i[a]!==o[u];)u--;for(;1<=a&&0<=u;a--,u--)if(i[a]!==o[u]){if(1!==a||1!==u)do{if(a--,0>--u||i[a]!==o[u]){var s=\"\\n\"+i[a].replace(\" at new \",\" at \");return e.displayName&&s.includes(\"<anonymous>\")&&(s=s.replace(\"<anonymous>\",e.displayName)),s}}while(1<=a&&0<=u);break}}}finally{j=!1,Error.prepareStackTrace=r}return(e=e?e.displayName||e.name:\"\")?z(e):\"\"}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_GlobalizeFormatter_SwitchThenStringExpression_NoErrors()
        {
            var input = "var _=[\"sun\",\"mon\",\"tue\",\"wed\",\"thu\",\"fri\",\"sat\"],S=function(e,t,r){var n=[],i=r.timeSeparator;return r.pattern.replace(y,(function(o){var u,s,l,c=o.charAt(0),d=o.length;switch(\"j\"===c&&(c=r.preferredTime),\"Z\"===c&&(d<4?(c=\"x\",d=4):d<5?(c=\"O\",d=4):(c=\"X\",d=5)),\"z\"===c&&(e.isDST&&(l=e.isDST()?r.daylightTzName:r.standardTzName),l||(c=\"O\",d<4&&(d=1))),c){case\"G\":l=r.eras[e.getFullYear()<0?0:1];break;case\"y\":l=e.getFullYear(),2===d&&(l=+(l=String(l)).substr(l.length-2));break;case\"Y\":(l=new Date(e.getTime())).setDate(l.getDate()+7-p(e,r.firstDay)-r.firstDay-r.minDays),l=l.getFullYear(),2===d&&(l=+(l=String(l)).substr(l.length-2));break;case\"Q\":case\"q\":l=Math.ceil((e.getMonth()+1)/3),d>2&&(l=r.quarters[c][d][l]);break;case\"M\":case\"L\":l=e.getMonth()+1,d>2&&(l=r.months[c][d][l]);break;case\"w\":l=p(v(e,\"year\"),r.firstDay),l=Math.ceil((g(e)+l)/7)-(7-l>=r.minDays?0:1);break;case\"W\":l=p(v(e,\"month\"),r.firstDay),l=Math.ceil((e.getDate()+l)/7)-(7-l>=r.minDays?0:1);break;case\"d\":l=e.getDate();break;case\"D\":l=g(e)+1;break;case\"F\":l=Math.floor(e.getDate()/7)+1;break;case\"e\":case\"c\":if(d<=2){l=p(e,r.firstDay)+1;break}case\"E\":l=_[e.getDay()],l=r.days[c][d][l];break;case\"a\":l=r.dayPeriods[e.getHours()<12?\"am\":\"pm\"];break;case\"h\":l=e.getHours()%12||12;break;case\"H\":l=e.getHours();break;case\"K\":l=e.getHours()%12;break;case\"k\":l=e.getHours()||24;break;case\"m\":l=e.getMinutes();break;case\"s\":l=e.getSeconds();break;case\"S\":l=Math.round(e.getMilliseconds()*Math.pow(10,d-3));break;case\"A\":l=Math.round(function(e){return e-v(e,\"day\")}(e)*Math.pow(10,d-3));break;case\"z\":break;case\"v\":if(r.genericTzName){l=r.genericTzName;break}case\"V\":if(r.timeZoneName){l=r.timeZoneName;break}\"v\"===o&&(d=1);case\"O\":0===e.getTimezoneOffset()?l=r.gmtZeroFormat:(d<4?(u=e.getTimezoneOffset(),u=r.hourFormat[u%60-u%1==0?0:1]):u=r.hourFormat,l=b(e,u,i,t),l=r.gmtFormat.replace(/\\{0\\}/,l));break;case\"X\":if(0===e.getTimezoneOffset()){l=\"Z\";break}case\"x\":u=e.getTimezoneOffset(),1===d&&u%60-u%1!=0&&(d+=1),4!==d&&5!==d||u%1!=0||(d-=2),l=b(e,l=[\"+HH;-HH\",\"+HHmm;-HHmm\",\"+HH:mm;-HH:mm\",\"+HHmmss;-HHmmss\",\"+HH:mm:ss;-HH:mm:ss\"][d-1],\":\");break;case\":\":l=i;break;case\"'\":l=a(o);break;default:l=o}\"number\"==typeof l&&(l=t[d](l)),\"literal\"===(s=m[c]||\"literal\")&&n.length&&\"literal\"===n[n.length-1].type?n[n.length-1].value+=l:n.push({type:s,value:l})})),n};";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_AnonymousClass_WithAdjacentMethods_AndComputedIterator_NoErrors()
        {
            var input = @"
                var UrlParams = class {
                    get(a) { var b = this.wa.get(a); return b || []; }
                    set(a, b) { this.Aa = null; this.wa.set(a, [b]); this.oa.set(a, this.Ba.Bc(b, a)); }
                    append(a, b) { const c = this.wa.get(a) || []; c.push(b); this.wa.set(a, c); }
                    [Symbol.iterator]() { const a = []; return a[Symbol.iterator](); }
                };
            ";

            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_WithStatement_SingleStatementClassDeclaration_ShouldFail()
        {
            var parser = CreateParser("with ({}) class C {}");
            parser.ParseProgram();
            Assert.Contains(parser.Errors, e => e.Contains("Declaration not allowed in with statement", StringComparison.OrdinalIgnoreCase));
        }
        [Fact]
        public void Parse_CompoundAssignment_PlusAssign()
        {
            var input = "var x = 1; x += 2;";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_NestedCompoundAssignments_NoErrors()
        {
            var input = "var r=0,n=0,e={pendingLanes:1,t:{lanes:0}}; r|=n&=e.pendingLanes,t.lanes=r;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_DoubleBangPostfixUpdate_NoErrors()
        {
            var input = "var o=0; function next(){return{done:!!o++}}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_RegexComputedMemberCall_AfterStrictEquals_NoErrors()
        {
            var input = "var b=Symbol.replace,C=!!/./[b]&&\"\"===/./[b](\"a\",\"$0\");";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XMain_ParenthesizedArrowCallback_WithParenStartedExpressionAfterIfBlock_NoErrors()
        {
            var input = "var out=s.map((e=>{if(e){return a}((x,y)=>y)(i,e)}));";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_FunctionExpression_Iife_WithTryCatchInBody_NoErrors()
        {
            var input = "(function(){try{return x}catch(e){return}}(a))";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_FunctionIife_WithIfTryCatchInCommaCondition_NoErrors()
        {
            var input = "function k(e,t,r,n,i,o){var a=[];if(function(e,t,r,n){e[s(cR)](t,r,n)}(e,t,r,n),a[Z]=(a[se]=l(Xi),a[de]=l(Xi),function(e,t,r,n,i){if(e)try{return e[s(ER)](t,r,n,i)[s(y_)]}catch(e){return}}(e,i,o,a[se],a[de])),a[Z])return[a[Z][l(Gn)],a[Z][d(Sn)],a[Z][d(du)],a[Z][l(Ba)]]}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_FunctionIife_WithTailForAndIfBlocks_BeforeCommaContinuation_NoErrors()
        {
            var input = "function q(){x&&(a=function(){if(n){for(var c=0;c<1;c++){var d=l(i[c],o[c],a);if(null!=d)return d;if(!0===r.isPropagationStopped())return}if(s)for(var f=0;f<1;f++){var h=l(i[f],o[f],u);if(null!=h)return h;if(!0===r.isPropagationStopped())return}else{var p=i[0],v=o[0];if(t.target===v)return l(p,v,u)}}}(),b)}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ConstObjectDestructuringDeclaration_WithInitializer_NoErrors()
        {
            var input = "const { feature_description, test } = feature_descriptionOrObject;";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);

            var declaration = Assert.IsType<LetStatement>(program.Statements.First());
            Assert.NotNull(declaration.DestructuringPattern);
            Assert.IsType<ObjectLiteral>(declaration.DestructuringPattern);
            Assert.NotNull(declaration.Value);
            Assert.IsType<Identifier>(declaration.Value);
        }

        [Fact]
        public void Parse_DestructuringParameter_WithOuterDefault_NoErrors()
        {
            var input = "function pick({ x } = { x: 5 }) { return x; }";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);

            var declaration = Assert.IsType<FunctionDeclarationStatement>(program.Statements.First());
            var parameter = Assert.Single(declaration.Function.Parameters);
            Assert.NotNull(parameter.DestructuringPattern);
            Assert.IsType<ObjectLiteral>(parameter.DestructuringPattern);
            Assert.NotNull(parameter.DefaultValue);
            Assert.IsType<ObjectLiteral>(parameter.DefaultValue);
        }

        [Fact]
        public void Parse_ArrowFunction_UseStrict_WithNonSimpleParams_ShouldFail()
        {
            var parser = CreateParser("var f = (a = 0) => { \"use strict\"; };");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("non-simple parameter list", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_AsyncArrowFunction_AwaitBindingIdentifier_ShouldFail()
        {
            var parser = CreateParser("async (await) => {}");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("await", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_AsyncArrowFunction_AwaitVarBindingInBody_ShouldFail()
        {
            var parser = CreateParser("async () => { var await; }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("await", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ArrowFunction_RestParameterWithDefault_ShouldFail()
        {
            var parser = CreateParser("(...args = []) => {}");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Rest parameter cannot have a default initializer", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ClassStaticBlock_AwaitIdentifierReference_ShouldFail()
        {
            var parser = CreateParser("class C { static { await; } }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("class static block", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ClassStaticBlock_AwaitBindingIdentifier_ShouldFail()
        {
            var parser = CreateParser("class C { static { class await {} } }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("class static block", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ForOf_ObjectRestNotLastInAssignmentTarget_ShouldFail()
        {
            var parser = CreateParser("for ({ ...rest, value } of items) {}");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Rest element must be last in object binding pattern", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ClassAccessor_ComputedNameWithInInsideForHead_NoErrors()
        {
            var input = @"
                var empty = {};
                var C, value;
                for (C = class { get ['x' in empty]() { return 'via get'; } }; ; ) {
                    value = C.prototype.false;
                    break;
                }
            ";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectAccessor_ComputedNameWithInInsideForHead_NoErrors()
        {
            var input = @"
                var empty = {};
                var obj, value;
                for (obj = { get ['x' in empty]() { return 'via get'; } }; ; ) {
                    value = obj.false;
                    break;
                }
            ";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_StrictModeLegacyOctalStringEscape_ShouldFail()
        {
            var parser = CreateParser("\"use strict\"; '\\1';");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Legacy octal literals are not allowed in strict mode", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_NonStrictLegacyOctalStringEscape_ZeroZero_UsesSingleNulCodeUnit()
        {
            var parser = CreateParser("'\\00';");
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            var statement = Assert.IsType<ExpressionStatement>(Assert.Single(program.Statements));
            var literal = Assert.IsType<StringLiteral>(statement.Expression);
            Assert.Equal("\0", literal.Value);
        }

        [Fact]
        public void Parse_StrictModeNonOctalDecimalStringEscape_ShouldFail()
        {
            var parser = CreateParser("\"use strict\"; '\\8';");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Legacy octal literals are not allowed in strict mode", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_StringLiteral_InvalidUnicodeEscape_ShouldFail()
        {
            var parser = CreateParser("'\\u';");
            parser.ParseProgram();

            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_StringLiteral_Allows_LineSeparatorLiteral()
        {
            var parser = CreateParser("\"\u2028\";");
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            var statement = Assert.IsType<ExpressionStatement>(Assert.Single(program.Statements));
            var literal = Assert.IsType<StringLiteral>(statement.Expression);
            Assert.Equal("\u2028", literal.Value);
        }

        [Fact]
        public void Parse_StrictModeTemplateExpressionLegacyOctalStringEscape_ShouldFail()
        {
            var parser = CreateParser("\"use strict\"; `${'\\07'}`;");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Legacy octal literals are not allowed in strict mode", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ClassField_SameLineWithoutSeparator_ShouldFail()
        {
            var parser = CreateParser("class C { x y }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Class fields must be separated", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ClassField_NameConstructor_ShouldFail()
        {
            var parser = CreateParser("class C { constructor; }");
            parser.ParseProgram();

            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_StaticClassField_NamePrototype_ShouldFail()
        {
            var parser = CreateParser("class C { static 'prototype'; }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Static class fields cannot be named 'prototype'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_LegacyOctalIntegerLiteral_NonStrict_UsesOctalValue()
        {
            var parser = CreateParser("070;");
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            var statement = Assert.IsType<ExpressionStatement>(Assert.Single(program.Statements));
            var literal = Assert.IsType<IntegerLiteral>(statement.Expression);
            Assert.Equal(56L, literal.Value);
        }

        [Fact]
        public void Parse_NonOctalDecimalIntegerLiteral_NonStrict_RemainsDecimal()
        {
            var parser = CreateParser("078;");
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            var statement = Assert.IsType<ExpressionStatement>(Assert.Single(program.Statements));
            var literal = Assert.IsType<IntegerLiteral>(statement.Expression);
            Assert.Equal(78L, literal.Value);
        }

        [Fact]
        public void Parse_UsingDeclaration_InIfSingleStatement_ShouldFail()
        {
            var parser = CreateParser("if (true) using x = null;");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("single-statement body", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_AwaitUsingDeclaration_WithoutInitializer_ShouldFail()
        {
            var parser = CreateParser("for (;false;) await using x;");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Missing initializer in const declaration", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_DuplicateExportedName_ShouldFail()
        {
            var parser = CreateModuleParser("var x; export { x }; export { x };");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Duplicate export", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_DuplicateTopLevelLexicalName_ShouldFail()
        {
            var parser = CreateModuleParser("function x() {} async function x() {}");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Duplicate declaration 'x' in module scope", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_ExportedBindingMustBeDeclared_ShouldFail()
        {
            var parser = CreateModuleParser("export { Number };");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("not declared in module scope", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_IllFormedStringExportName_ShouldFail()
        {
            var parser = CreateModuleParser("export {Moon as \"\\uD83C\"} from \"./mod.js\"; function Moon() {}");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Ill-formed Unicode string", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_HtmlOpenComment_ShouldFail()
        {
            var parser = CreateModuleParser("<!--");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("HTML-like comments", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_HtmlCloseComment_ShouldFail()
        {
            var parser = CreateModuleParser("-->");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("HTML-like comments", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_DuplicateLabel_ShouldFail()
        {
            var parser = CreateModuleParser("label: { label: 0; }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Duplicate label 'label'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_UndefinedBreakTarget_ShouldFail()
        {
            var parser = CreateModuleParser("while (false) { break undef; }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Undefined break target 'undef'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_UndefinedContinueTarget_ShouldFail()
        {
            var parser = CreateModuleParser("while (false) { continue undef; }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Undefined continue target 'undef'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_ExportStarAsStringName_ShouldSucceed()
        {
            var parser = CreateModuleParser("export * as \"All\" from \"./mod.js\";");
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_Module_QuotedStarExport_UnpairedSurrogate_ShouldFail()
        {
            var parser = CreateModuleParser("export \"*\" as \"\\uD83D\" from \"./mod.js\";");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Ill-formed Unicode string", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_XVendor_GlobalizeWrapper_WithDirectiveAndGroupedFactory_NoErrors()
        {
            var input = """
                !function(t,n){"use strict";"function"==typeof define&&define.amd?define(["../globalize-runtime","./number"],n):e.exports=n(r(34047),r(993146))}(0,(function(e){var t=e._formatMessage,r=e._runtimeKey,n=e._validateParameterPresence,i=e._validateParameterTypeNumber,o=function(e,r,n){var i,o,a=n.displayNames||{},u=n.unitPatterns;return i=a["displayName-count-"+r]||a["displayName-count-other"]||a.displayName||n.currency,o=u["unitPattern-count-"+r]||u["unitPattern-count-other"],t(o,[e,i])};return e._currencyFormatterFn=function(e,t,r){return t&&r?function(a){return n(a,"value"),i(a,"value"),o(e(a),t(a),r)}:function(t){return e(t)}},e._currencyNameFormat=o,e.currencyFormatter=e.prototype.currencyFormatter=function(t,n){return n=n||{},e[r("currencyFormatter",this._locale,[t,n])]},e.formatCurrency=e.prototype.formatCurrency=function(e,t,r){return n(e,"value"),i(e,"value"),this.currencyFormatter(t,r)(e)},e}))
                """;
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_UrlPrototype_ParseHost_Helper_NoErrors()
        {
            var input = """
                var x={parse:function(e,t,r){var i,o,a,u,s,l=this,c=t||ge,d=0,f="",p=!1,m=!1,y=!1;for(e=b(e),t||(l.scheme="",l.username="",l.password="",l.host=null,l.port=null,l.path=[],l.query=null,l.fragment=null,l.cannotBeABaseURL=!1,e=j(e,ne,""),e=j(e,ie,"$1")),e=j(e,oe,""),i=v(e);d<=i.length;){switch(o=i[d],c){case ge:if(!o||!D(G,o)){if(t)return W;c=ye;continue}f+=H(o),c=me;break;case me:if(o&&(D($,o)||"+"===o||"-"===o||"."===o))f+=H(o);else{if(":"!==o){if(t)return W;f="",c=ye,d=0;continue}if(t&&(l.isSpecial()!==h(fe,f)||"file"===f&&(l.includesCredentials()||null!==l.port)||"file"===l.scheme&&!l.host))return;if(l.scheme=f,t)return void(l.isSpecial()&&fe[l.scheme]===l.port&&(l.port=null));f="","file"===l.scheme?c=Ie:l.isSpecial()&&r&&r.scheme===l.scheme?c=be:l.isSpecial()?c=Ee:"/"===i[d+1]?(c=_e,d++):(l.cannotBeABaseURL=!0,z(l.path,""),c=De)}break;case Fe:o!==n&&(l.fragment+=de(o,se))}d++}},parseHost:function(e){var t,r,n;if("["===N(e,0)){if("]"!==N(e,e.length-1))return q;if(t=function(e){var t,r,n,i,o,a,u,s=[0,0,0,0,0,0,0,0],l=0,c=null,d=0,f=function(){return N(e,d)};if(":"===f()){if(":"!==N(e,1))return;d+=2,c=++l}for(;f();){if(8===l)return;if(":"!==f()){for(t=r=0;r<4&&D(ee,f());)t=16*t+O(f(),16),d++,r++;if("."===f()){if(0===r)return;if(d-=r,l>6)return;for(n=0;f();){if(i=null,n>0){if(!("."===f()&&n<4))return;d++}if(!D(Y,f()))return;for(;D(Y,f());){if(o=O(f(),10),null===i)i=o;else{if(0===i)return;i=10*i+o}if(i>255)return;d++}s[l]=256*s[l]+i,2!=++n&&4!==n||l++}if(4!==n)return;break}if(":"===f()){if(d++,!f())return}else if(f())return;s[l++]=t}else{if(null!==c)return;d++,c=++l}}if(null!==c)for(a=l-c,l=7;0!==l&&a>0;)u=s[l],s[l--]=s[c+a-1],s[c+--a]=u;else if(8!==l)return;return s}(B(e,1,-1)),!t)return q;this.host=t}};
                """;
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_ParenthesizedModuleMapProperty_WithNestedFunctionBodies_NoErrors()
        {
            var input = """
                ({1:(e,t,r)=>{const B=function(e,t,r,{pure:n,areStatesEqual:i=V,areOwnPropsEqual:o=F,areStatePropsEqual:a=F,areMergedPropsEqual:u=F,forwardRef:l=!1,context:c=f}={}){const d=c,h=function(e){return e?"function"==typeof e?I(e):O(e,"mapStateToProps"):C((()=>({})))}(e),p=function(e){return e&&"object"==typeof e?C((t=>function(e,t){const r={};for(const n in e){const i=e[n];"function"==typeof i&&(r[n]=(...e)=>t(i(...e)))}return r}(e,t))):e?"function"==typeof e?I(e):O(e,"mapDispatchToProps"):C((e=>({dispatch:e})))}(t),v=function(e){return e?"function"==typeof e?function(e){return function(t,{displayName:r,areMergedPropsEqual:n}){let i,o=!1;return function(t,r,a){const u=e(t,r,a);return o?n(u,i)||(i=u):(o=!0,i=u),i}}}(e):O(e,"mergeProps"):()=>P}(r),g=Boolean(e);return e=>{const t=e.displayName||e.name||"Component",r=`Connect(${t})`,n={shouldHandleStateChanges:g,displayName:r,wrappedComponentName:t,WrappedComponent:e,initMapStateToProps:h,initMapDispatchToProps:p,initMergeProps:v,areStatesEqual:i,areStatePropsEqual:a,areOwnPropsEqual:o,areMergedPropsEqual:u};function c(t){const[r,i,o]=s.useMemo((()=>{const{reactReduxForwardedRef:e}=t,r=(0,S.Z)(t,L);return[t.context,e,r]}),[t]),a=s.useMemo((()=>r&&r.Consumer&&(0,R.isContextConsumer)(s.createElement(r.Consumer,null))?r:d),[r,d]),u=s.useContext(a),l=Boolean(t.store)&&Boolean(t.store.getState)&&Boolean(t.store.dispatch),c=Boolean(u)&&Boolean(u.store);const f=l?t.store:u.store,h=c?u.getServerState:f.getState,p=s.useMemo((()=>function(e,t){let{initMapStateToProps:r,initMapDispatchToProps:n,initMergeProps:i}=t,o=(0,S.Z)(t,k);return x(r(e,o),n(e,o),i(e,o),e,o)}(f.dispatch,n)),[f]),[v,m]=s.useMemo((()=>{if(!g)return j;const e=N(f,l?void 0:u.subscription),t=e.notifyNestedSubs.bind(e);return[e,t]}),[f,l,u]),y=s.useMemo((()=>l?u:(0,_.Z)({},u,{subscription:v})),[l,u,v]),b=s.useRef(),w=s.useRef(o),E=s.useRef(),C=s.useRef(!1),T=(s.useRef(!1),s.useRef(!1)),I=s.useRef();D((()=>(T.current=!0,()=>{T.current=!1})),[]);const O=s.useMemo((()=>()=>E.current&&o===w.current?E.current:p(f.getState(),o)),[f,o]),P=s.useMemo((()=>e=>v?function(e,t,r,n,i,o,a,u,s,l,c){if(!e)return()=>{};let d=!1,f=null;const h=()=>{if(d||!u.current)return;const e=t.getState();let r,h;try{r=n(e,i.current)}catch(e){h=e,f=e}h||(f=null),r===o.current?a.current||l():(o.current=r,s.current=r,a.current=!0,c())};return r.onStateChange=h,r.trySubscribe(),h(),()=>{if(d=!0,r.tryUnsubscribe(),r.onStateChange=null,f)throw f}}(g,f,v,p,w,b,C,T,E,m,e):()=>{}),[v]);var A,M,F;let V;A=U,M=[w,b,C,o,E,m],D((()=>A(...M)),F);try{V=z(P,O,h?()=>p(h(),o):O)}catch(e){throw I.current&&(e.message+=`\nThe error may be correlated with this previous error:\n${I.current.stack}\n\n`),e}D((()=>{I.current=void 0,E.current=void 0,b.current=V}));const B=s.useMemo((()=>s.createElement(e,(0,_.Z)({},V,{ref:i}))),[i,e,V]);return s.useMemo((()=>g?s.createElement(a.Provider,{value:y},B):B),[a,B,y])}const f=s.memo(c);if(f.WrappedComponent=e,f.displayName=c.displayName=r,l){const t=s.forwardRef((function(e,t){return s.createElement(f,(0,_.Z)({},e,{reactReduxForwardedRef:t}))}));return t.displayName=r,t.WrappedComponent=e,E()(t,e)}return E()(f,e)}};const H=function({store:e,context:t,children:r,serverState:n,stabilityCheck:i="once",noopCheck:o="once"}){const a=s.useMemo((()=>{const t=N(e);return{store:e,subscription:t,getServerState:n?()=>n:void 0,stabilityCheck:i,noopCheck:o}}),[e,n,i,o]),u=s.useMemo((()=>e.getState()),[e]);D((()=>{const{subscription:t}=a;return t.onStateChange=t.notifyNestedSubs,t.trySubscribe(),u!==e.getState()&&t.notifyNestedSubs(),()=>{t.tryUnsubscribe(),t.onStateChange=void 0}}),[a,u]);const l=t||f;return s.createElement(l.Provider,{value:a},r)}})
                """;
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_TemplateInterpolationFollowedByBlockStatement_InReturnedArrowBody_NoErrors()
        {
            var input = """
                ({1:(e,t,r)=>{return e=>{const r=`Connect(${t})`;if(l){return y}return z}}});
                """;
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_RuntimeMode_IfWithEmptyConsequentElseBlock_NoErrors()
        {
            var input = """
                function hasInlineImage(e,t){if(!c(t))return!1;const{editorStateRaw:n}=e;if(n)for(const e of n.blocks){var i;if(null!==(i=e.inlineStyleRanges)&&void 0!==i&&i.length)return!0;if(e.type!==r.UP);else{const t=e.entityRanges;if(!Array.isArray(t)||!t.length)continue;const[i]=t,r=String(i.key),s=n.entityMap[r];if(!s)continue;if(s.type===o.Z.INLINE_IMAGE)return!0}}return p}
                """;
            var parser = CreateRuntimeParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_RuntimeMode_ArrowFunction_WithEmptyConsequentElseBlock_NoErrors()
        {
            var input = """
                const scrollTo=(e,t,r)=>{if("number"==typeof e);else{var n=e||x;e=n.x,t=n.y,r=n.animated}return [e,t,r]}
                """;
            var parser = CreateRuntimeParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_LatestVendorFile_NoErrors()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "vendor.f1dc7e4a.latest.js");
            var input = File.ReadAllText(path);
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_AssignmentToIifeIndexedResult_NoErrors()
        {
            var input = """
                function o(e){return A[0|e]}
                function i(e){return e}
                function r(e,t){return 0}
                function u(e){var t=[];return t[se]=WK[ue][e],void 0!==t[se]?t[se]:WK[ue][e]=function(e){var t=[];t[se]=ce,t[de]=e;for(var n=se;n<t[de].length;n+=ge)e=((e=((e=((e=r(t[de].substr(n,ge),fe))^ve)&he)>>>pe|e<<fe-pe)&he)>>>Z|e<<fe-Z)&he,t[se]+=i(e);return t[se]}(e)}
                function s(e){var t=[];return t[se]=WK[le][e],void 0!==t[se]?t[se]:WK[le][e]=function(e){return e}(e)}
                """;
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }
    }
}



