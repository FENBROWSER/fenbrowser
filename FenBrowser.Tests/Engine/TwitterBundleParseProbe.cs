using System;
using System.IO;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Engine
{
    // Iterative probe: parse the staged x.com main bundle and print the first
    // few errors with byte-offset context, so we can pinpoint which production
    // the Pratt parser is choking on. Not a regression test — diagnostic only.
    public class TwitterBundleParseProbe
    {
        private readonly ITestOutputHelper _output;
        public TwitterBundleParseProbe(ITestOutputHelper output) => _output = output;

        private static void Run(ITestOutputHelper output, string label, string js)
        {
            var lexer = new Lexer(js);
            var parser = new Parser(lexer, isModule: false, allowRecovery: false);
            try { parser.ParseProgram(); }
            catch (Exception ex) { output.WriteLine($"[{label}] threw: {ex.Message}"); }
            output.WriteLine($"[{label}] errors: {parser.Errors.Count}");
            for (int i = 0; i < Math.Min(parser.Errors.Count, 3); i++)
                output.WriteLine($"  - {parser.Errors[i]}");
        }

        [Fact]
        public void Reduce_VarArrowSwitchTemplate()
        {
            // Mirrors the x.com bundle pattern at col 669851:
            //   var r = e => { switch(e){...; default: (0,n.default)(`tag ${e}`); return} },
            //       l = e => { switch(e){...} };
            Run(_output, "minimal", "var r=e=>{switch(e){case 'a':return 'A';default:(0,n.default)(`x ${e}`);return}},l=e=>1;");
            Run(_output, "no_template", "var r=e=>{switch(e){case 'a':return 'A';default:(0,n.default)(e);return}},l=e=>1;");
            Run(_output, "no_default_in_switch", "var r=e=>{switch(e){case 'a':return 'A';case 'b':(0,n.default)(`x ${e}`);return}},l=e=>1;");
            Run(_output, "no_switch", "var r=e=>{(0,n.default)(`x ${e}`);return},l=e=>1;");
            Run(_output, "no_inner_comma", "var r=e=>{switch(e){case 'a':return 'A';default:n.default(`x ${e}`);return}},l=e=>1;");
            Run(_output, "no_template_keep_comma", "var r=e=>{switch(e){default:(0,n.default)(e);return}},l=e=>1;");
            Run(_output, "switch_default_only", "var r=e=>{switch(e){default:return 1}},l=e=>1;");

            // Drill into template literal handling
            Run(_output, "tmpl_in_body", "var r=e=>{f(`x ${e}`);return},l=1;");
            Run(_output, "tmpl_no_subst_in_body", "var r=e=>{f(`x`);return},l=1;");
            Run(_output, "tmpl_then_stmt", "var r=()=>{f(`x ${1}`);return 1};");
            Run(_output, "tmpl_alone", "f(`x ${e}`);");
            Run(_output, "tmpl_then_semicolon_then_stmt", "f(`x ${1}`);g();");
            Run(_output, "tmpl_then_var", "f(`x ${1}`);var z=1;");
            Run(_output, "tmpl_then_return_top", "function r(){f(`x ${1}`);return}");
            Run(_output, "tmpl_then_return_then_var", "function r(){f(`x ${1}`);return};var l=1;");
            Run(_output, "tmpl_then_return_no_semicolon", "function r(){f(`x ${1}`)\nreturn};var l=1;");
            Run(_output, "arrow_tmpl_then_return_then_comma", "var r=()=>{f(`x ${1}`);return},l=1;");
            Run(_output, "arrow_no_tmpl_then_return_then_comma", "var r=()=>{f(`x`);return},l=1;");
            Run(_output, "arrow_no_call_tmpl", "var r=()=>{var z=`x ${1}`;return},l=1;");

            // No-call: even simpler reductions
            Run(_output, "ultra_min_arrow_tmpl", "var r=()=>{`${1}`};l=1;");
            Run(_output, "ultra_min_arrow_tmpl_comma", "var r=()=>{`${1}`},l=1;");
            Run(_output, "fn_expr_tmpl_comma", "var r=function(){`${1}`},l=1;");
            Run(_output, "ultra_min_arrow_str_comma", "var r=()=>{`hi`},l=1;");
        }

        [Fact]
        public void Reduce_Round2()
        {
            // From round 1: `var r=()=>{`${1}`},l=1;` PASSES
            // but `var r=()=>{`${1}`;return},l=1;` we did not test.
            Run(_output, "r2_tmpl_then_semi", "var r=()=>{`${1}`;return},l=1;");
            Run(_output, "r2_no_subst_then_semi", "var r=()=>{`x`;return},l=1;");
            Run(_output, "r2_tmpl_no_return", "var r=()=>{`${1}`;0},l=1;");
            Run(_output, "r2_tmpl_then_zero", "var r=()=>{`${1}`;0;},l=1;");
            Run(_output, "r2_tmpl_then_break", "var r=()=>{`${1}`;a=1},l=1;");
            Run(_output, "r2_tmpl_then_assign", "var r=()=>{`${1}`;a=1;},l=1;");
            Run(_output, "r2_only_tmpl_semi", "var r=()=>{`${1}`;},l=1;");

            // Round 3: webpack module map — numeric property names as shorthand methods
            Run(_output, "r3_numeric_shorthand_method", "var m={918692(e,t,r){return 1}};");
            Run(_output, "r3_numeric_shorthand_two", "var m={918692(e,t,r){return 1},786739(a){return a}};");
            Run(_output, "r3_string_shorthand_method", "var m={'foo'(e){return 1}};");
            Run(_output, "r3_computed_shorthand_method", "var m={[1](e){return 1}};");
            Run(_output, "r3_numeric_key_value", "var m={918692:1};"); // baseline: numeric key+value works?

            // Round 4 — slice approximating the bytes around col 674972 of x.com bundle
            Run(_output, "r4_slice_real",
                "var m={k(e,t,r){const{a:r,b:n,c:o,d:s}=t;return{x:{y:(0,a.kf)(e,s),z:n,w:r?(0,i.A)(e,r):void 0,v:o?(0,i.A)(e,o):void 0}};default:(0,n.default)(`x ${r}`);return}};(O.getOwnPropertyDescriptor(s,'name')||{}).writable||O.defineProperty(s,'name',{value:'default',configurable:!0})},918692(e,t,r){'use strict';return 1}};");

            // Try without the default branch
            Run(_output, "r4_no_default",
                "var m={k(e,t,r){const{a:r,b:n}=t;return{x:{y:(0,a.kf)(e,s),z:n}};return}};(O.f(s,'name')||{}).writable||O.g(s,'name',{value:'default',configurable:!0})},918692(e,t,r){return 1}};");

            // Even simpler
            Run(_output, "r4_destruct_in_method",
                "var m={k(){const{a:r,b:n}=t;return{x:{y:n}};return}},918692(){return 1}};");
            Run(_output, "r4_just_destructure",
                "var m={k(){const{a:r,b:n}=t;return}},918692(){return 1}};");
            Run(_output, "r4_no_destructure",
                "var m={k(){return}},918692(){return 1}};");

            // Round 5 — properly balanced webpack module map shapes
            Run(_output, "r5_simple", "var m={k(){return},n(){return 1}};");
            Run(_output, "r5_numeric_second", "var m={k(){return},918692(){return 1}};");
            Run(_output, "r5_paren_in_body", "var m={k(){(0,n.f)(s,'n',{v:'d'})},918692(){return 1}};");
            Run(_output, "r5_iife_in_body", "var m={k(){(()=>{})()},918692(){return 1}};");
            Run(_output, "r5_logical_or_obj", "var m={k(){(a||{}).w||b()},918692(){return 1}};");
            Run(_output, "r5_real_method_body",
                "var m={k(){(O.f(s,'name')||{}).writable||O.g(s,'name',{value:'default',configurable:!0})},918692(){return 1}};");
        }

        // Walks the bundle character-by-character (string-/template-/comment-/regex-aware)
        // to find webpack module-map entries (`,<digits>(`) at top depth, then tries
        // to parse each module body in isolation. Reports the first N that fail.
        [Fact]
        public void Probe_PerModuleParse()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", "twitter_main.js");
            var src = File.ReadAllText(path);
            var entries = FindModuleEntries(src);
            _output.WriteLine($"found {entries.Count} module entries");

            int failed = 0;
            foreach (var (id, idStart, bodyStart, bodyEnd) in entries)
            {
                // Wrap as standalone expression so it's parseable on its own.
                var moduleBody = src.Substring(bodyStart, bodyEnd - bodyStart);
                var wrapped = "(function(e,t,r){" + moduleBody + "})";
                var lexer = new Lexer(wrapped);
                var parser = new Parser(lexer, false, allowRecovery: false);
                try { parser.ParseProgram(); } catch { }
                if (parser.Errors.Count > 0)
                {
                    failed++;
                    _output.WriteLine($"FAIL module {id} @byte {idStart} body={bodyEnd - bodyStart} bytes — {parser.Errors[0]}");
                    if (failed >= 8) break;
                }
            }
            _output.WriteLine($"failed modules in first scan: {failed}");
            // Show modules that bracket the known failure cols
            int[] cols = { 674972, 716554, 716758, 716849 };
            foreach (var col in cols)
            {
                var hit = entries.FirstOrDefault(e => e.bodyStart <= col && col <= e.bodyEnd);
                if (hit.id != null)
                    _output.WriteLine($"col {col} → module {hit.id} body [{hit.bodyStart}..{hit.bodyEnd}] size {hit.bodyEnd - hit.bodyStart}");
                else
                {
                    // Find nearest module before
                    var before = entries.LastOrDefault(e => e.bodyEnd < col);
                    var after = entries.FirstOrDefault(e => e.bodyStart > col);
                    _output.WriteLine($"col {col} → between modules {before.id} (ends {before.bodyEnd}) and {after.id} (starts {after.idStart})");
                }
            }
        }

        [Fact]
        public void Probe_FindPairwiseDesync()
        {
            // Module 918692 (index 402) fails in sequence but not in isolation.
            // Suggests prior module corrupts parser state. Bisect: parse just
            // {moduleN(...){body},918692(...){body}} for various N.
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", "twitter_main.js");
            var src = File.ReadAllText(path);
            var entries = FindModuleEntries(src);
            var target = entries.First(e => e.id == "918692");
            int targetIdx = entries.IndexOf(target);
            _output.WriteLine($"target idx={targetIdx} id={target.id}");

            bool TwoOk(int n)
            {
                var prev = entries[n];
                var sb = new System.Text.StringBuilder();
                sb.Append("(function(){var m={");
                sb.Append(prev.id).Append("(e,t,r){").Append(src, prev.bodyStart, prev.bodyEnd - prev.bodyStart).Append("},");
                sb.Append(target.id).Append("(e,t,r){").Append(src, target.bodyStart, target.bodyEnd - target.bodyStart).Append('}');
                sb.Append("};})();");
                var l = new Lexer(sb.ToString()); var p = new Parser(l, false, allowRecovery: false);
                try { p.ParseProgram(); } catch { }
                return p.Errors.Count == 0;
            }

            // Walk backward from targetIdx-1 to find first prev that causes failure
            int badPrev = -1;
            for (int n = targetIdx - 1; n >= Math.Max(0, targetIdx - 30); n--)
            {
                if (!TwoOk(n)) { badPrev = n; _output.WriteLine($"prev idx {n} (id {entries[n].id}) → FAIL when paired with target"); }
                else _output.WriteLine($"prev idx {n} (id {entries[n].id}) → OK");
            }
            _output.WriteLine($"first bad prev: {badPrev}");
        }

        [Fact]
        public void Probe_BisectInsideBadModule()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", "twitter_main.js");
            var src = File.ReadAllText(path);
            var entries = FindModuleEntries(src);
            // entries field reused below for "exact_pair" test
            var bad = entries.First(e => e.id == "344102");
            var body = src.Substring(bad.bodyStart, bad.bodyEnd - bad.bodyStart);
            _output.WriteLine($"module {bad.id} body length: {body.Length}");
            _output.WriteLine($"body[0..200]: {body.Substring(0, Math.Min(200, body.Length))}");
            _output.WriteLine($"body[tail 100]: ...{body.Substring(Math.Max(0, body.Length - 100))}");

            // First confirm: this body alone fails when wrapped as standalone function
            {
                var standalone = "(function(e,t,r){" + body + "})";
                var l = new Lexer(standalone); var p = new Parser(l, false, allowRecovery: false);
                try { p.ParseProgram(); } catch { }
                _output.WriteLine($"standalone errors: {p.Errors.Count}");

                // Sanity reductions for the formal-param/lexical conflict bug
                Run(_output, "param-conflict basic", "(function(e,t,r){\"use strict\";let t;})");
                Run(_output, "param-conflict shorthand", "var m={k(e,t,r){\"use strict\";let t;}};");
                Run(_output, "param-conflict no-strict", "(function(e,t,r){let t;})");
                Run(_output, "param-conflict shorthand-no-strict", "var m={k(e,t,r){let t;}};");

                // Reductions for module 918692 — rest in object destructuring
                Run(_output, "rest_in_obj_destruct_let", "let{a:r,...s}=t;");
                Run(_output, "rest_in_obj_destruct_var", "var{a:r,...s}=t;");
                Run(_output, "rest_in_obj_destruct_const", "const{a:r,...s}=t;");
                Run(_output, "obj_spread", "var x={...s,b:1};");
                Run(_output, "rest_shorthand_method", "var m={k(e,t,r){let{a:r,...s}=t;return{...s}}};");

                // Module 344102 reduction — arrow function with switch+default+template,
                // followed by Object.defineProperty extender, then next module
                Run(_output, "r6_pair", "var m={k(e,t,r){let s=(e,t)=>{let{type:r}=t;switch(t.type){case\"A\":{let{x}=t;return{a:{x}}}default:(0,n.f)(`x ${r}`);return}};(Object.gOPD(s,\"name\")||{}).w||Object.dP(s,\"name\",{v:\"d\",c:!0})},next(e,t,r){return 1}};");
                Run(_output, "r6_pair_no_switch", "var m={k(e,t,r){let s=(e,t)=>{return 1};(Object.gOPD(s,\"name\")||{}).w||Object.dP(s,\"name\",{v:\"d\"})},next(){return 1}};");
                Run(_output, "r6_pair_no_dp", "var m={k(e,t,r){let s=(e,t)=>{let{type:r}=t;switch(t.type){case\"A\":return 1;default:(0,n.f)(`x ${r}`);return}}},next(){return 1}};");
                Run(_output, "r6_minimal", "var m={k(){let s=(e,t)=>{switch(e){default:(0,n.f)(`x ${t}`);return}};(a||{}).w||b()},next(){return 1}};");

                // Try the EXACT pair from the bundle (344102 + 918692)
                var prev = entries.First(e => e.id == "344102");
                var next = entries.First(e => e.id == "918692");
                var prevBody = src.Substring(prev.bodyStart, prev.bodyEnd - prev.bodyStart);
                var nextBody = src.Substring(next.bodyStart, next.bodyEnd - next.bodyStart);
                Run(_output, "exact_pair",
                    "var m={" + prev.id + "(e,t,r){" + prevBody + "}," + next.id + "(e,t,r){" + nextBody + "}};");
                // Try with just prev + minimal next
                Run(_output, "pair_minnext",
                    "var m={" + prev.id + "(e,t,r){" + prevBody + "}," + next.id + "(){return 1}};");
                // Try with just prev alone (no pairing)
                Run(_output, "prev_alone",
                    "var m={" + prev.id + "(e,t,r){" + prevBody + "}};");

                // Bisect at safe (top-depth) boundaries within prevBody. Use semicolons
                // at depth 0 (relative to body) as cut points. Find first cut that causes failure.
                _output.WriteLine("--- bisecting prevBody at depth-0 semicolons ---");
                var safePoints = new System.Collections.Generic.List<int>();
                {
                    int d = 0; char pv = '\0'; var ts = new System.Collections.Generic.Stack<int>();
                    for (int i = 0; i < prevBody.Length; i++)
                    {
                        char ch = prevBody[i];
                        if (ts.Count > 0)
                        {
                            if (ch == '{') { ts.Push(ts.Pop() + 1); d++; pv = ch; continue; }
                            if (ch == '}') { int top = ts.Pop(); if (top == 0) { i = SkipTemplateText(prevBody, i + 1, ts); pv = '`'; continue; } ts.Push(top - 1); d--; pv = ch; continue; }
                        }
                        switch (ch)
                        {
                            case '"': case '\'': i = SkipString(prevBody, i + 1, ch); pv = ch; break;
                            case '`': i = SkipTemplateText(prevBody, i + 1, ts); pv = '`'; break;
                            case '/':
                                if (i + 1 < prevBody.Length && prevBody[i+1] == '/') { while (i < prevBody.Length && prevBody[i] != '\n') i++; continue; }
                                if (i + 1 < prevBody.Length && prevBody[i+1] == '*') { i += 2; while (i + 1 < prevBody.Length && !(prevBody[i] == '*' && prevBody[i+1] == '/')) i++; i++; continue; }
                                if (IsRegexContext2(pv)) { i = SkipRegex(prevBody, i + 1); pv = '/'; continue; }
                                pv = ch; break;
                            case '{': d++; pv = ch; break;
                            case '}': d--; pv = ch; break;
                            case ';': if (d == 0) safePoints.Add(i + 1); pv = ch; break;
                            default: if (!char.IsWhiteSpace(ch)) pv = ch; break;
                        }
                    }
                }
                _output.WriteLine($"safe cut points: {safePoints.Count}");
                foreach (var sp in safePoints) _output.WriteLine($"  safepoint @ {sp}: ...{prevBody.Substring(Math.Max(0, sp - 40), Math.Min(40, sp))}");
                int lastOk = 0, firstFail = -1;
                foreach (var cut in safePoints)
                {
                    var truncated = prevBody.Substring(0, cut);
                    var pairTrunc = "var m={" + prev.id + "(e,t,r){" + truncated + "},next(){return 1}};";
                    var l3 = new Lexer(pairTrunc); var p3 = new Parser(l3, false, allowRecovery: false);
                    try { p3.ParseProgram(); } catch { }
                    var ok = p3.Errors.Count == 0;
                    if (ok) lastOk = cut;
                    else { firstFail = cut; _output.WriteLine($"  cut {cut}: FAIL — last 60: ...{truncated.Substring(Math.Max(0, cut - 60))}"); break; }
                }
                if (firstFail < 0) _output.WriteLine($"  all cuts OK; lastOk={lastOk}");
                else _output.WriteLine($"  desync introduced by chars [{lastOk}..{firstFail}]: {prevBody.Substring(lastOk, firstFail - lastOk)}");

                // Suspect is the trailing un-semicoloned expression after byte 612.
                var suffix = prevBody.Substring(612);
                _output.WriteLine($"suffix (no-semi expr) [{suffix.Length} chars]: {suffix}");
                // Try the suffix as the SOLE body of a paired method
                Run(_output, "suffix_only", "var m={k(){" + suffix + "},next(){return 1}};");
                // Try with trailing ; appended
                Run(_output, "suffix_only_semi", "var m={k(){" + suffix + ";},next(){return 1}};");

                // Combine prefix subsets + suffix
                var prefix0_30 = prevBody.Substring(0, 30);       // "use strict";r.d(...)
                var prefix0_81 = prevBody.Substring(0, 81);
                var prefix0_612 = prevBody.Substring(0, 612);
                Run(_output, "p0_30+suffix", "var m={k(){" + prefix0_30 + suffix + "},next(){return 1}};");
                Run(_output, "p0_81+suffix", "var m={k(){" + prefix0_81 + suffix + "},next(){return 1}};");
                Run(_output, "p0_612+suffix", "var m={k(){" + prefix0_612 + suffix + "},next(){return 1}};");
                // The let-arrow chunk alone with suffix
                var letArrow = prevBody.Substring(81, 612 - 81); // "let s=(e,t)=>{...};"
                Run(_output, "letArrow+suffix", "var m={k(){" + letArrow + suffix + "},next(){return 1}};");
                Run(_output, "letArrow_only", "var m={k(){" + letArrow + "},next(){return 1}};");

                // Isolate which difference between exact_pair (fail) and p0_612+suffix (pass) matters
                Run(_output, "with_prevId_params", "var m={" + prev.id + "(e,t,r){" + prevBody + "},next(){return 1}};");
                Run(_output, "with_k_etr_params", "var m={k(e,t,r){" + prevBody + "},next(){return 1}};");
                Run(_output, "with_k_no_params", "var m={k(){" + prevBody + "},next(){return 1}};");
                Run(_output, "with_realnextbody", "var m={k(){" + prevBody + "}," + next.id + "(e,t,r){" + nextBody + "}};");
                Run(_output, "with_k_etr_realnext", "var m={k(e,t,r){" + prevBody + "}," + next.id + "(e,t,r){" + nextBody + "}};");
                Run(_output, "swap_order", "var m={" + next.id + "(e,t,r){" + nextBody + "}," + prev.id + "(e,t,r){" + prevBody + "}};");
                Run(_output, "two_prevs", "var m={" + prev.id + "(e,t,r){" + prevBody + "},k(e,t,r){" + prevBody + "}};");
                Run(_output, "two_nexts", "var m={k(e,t,r){" + nextBody + "}," + next.id + "(e,t,r){" + nextBody + "}};");
                Run(_output, "next_alone_empty_prev", "var m={k(){},next(){" + nextBody + "}};");
                Run(_output, "next_alone_empty_prev_etr", "var m={k(e,t,r){},next(e,t,r){" + nextBody + "}};");
                Run(_output, "k_minimal_prev_real_next", "var m={k(e,t,r){return},next(e,t,r){" + nextBody + "}};");

                // What if the SECOND method name is the trigger?
                Run(_output, "second_is_numeric_simple", "var m={k(e,t,r){" + prevBody + "},123(e,t,r){return 1}};");
                Run(_output, "second_is_numeric_realnext", "var m={k(e,t,r){" + prevBody + "},123(e,t,r){" + nextBody + "}};");
                Run(_output, "second_is_string_realnext", "var m={k(e,t,r){" + prevBody + "},'abc'(e,t,r){" + nextBody + "}};");
                Run(_output, "second_is_ident_realnext", "var m={k(e,t,r){" + prevBody + "},abc(e,t,r){" + nextBody + "}};");

                // Trigger pattern: numeric-method-after-X where X's body has SOMETHING.
                // Walk increasing prevBody prefix at depth-0 semis and find which one makes
                // 2nd numeric method fail.
                _output.WriteLine("--- bisect prev for numeric-second fail ---");
                foreach (var cut in safePoints)
                {
                    var pb = prevBody.Substring(0, cut);
                    var s = "var m={k(e,t,r){" + pb + "},918692(){return 1}};";
                    var lex = new Lexer(s); var par = new Parser(lex, false, allowRecovery: false);
                    try { par.ParseProgram(); } catch { }
                    var ok = par.Errors.Count == 0;
                    _output.WriteLine($"  prevCut {cut}: {(ok ? "OK" : "FAIL")} — prev tail: ...{pb.Substring(Math.Max(0, cut - 50))}");
                }
                // Try with FULL prevBody (no semi-cut)
                {
                    var s = "var m={k(e,t,r){" + prevBody + "},918692(){return 1}};";
                    var lex = new Lexer(s); var par = new Parser(lex, false, allowRecovery: false);
                    try { par.ParseProgram(); } catch { }
                    var ok = par.Errors.Count == 0;
                    _output.WriteLine($"  FULL prev: {(ok ? "OK" : "FAIL")}");
                }

                // Bisect inside the let-arrow chunk (bytes 81..612)
                _output.WriteLine("--- bisect arrow body (bytes 81..612) ---");
                var arrowChunk = prevBody.Substring(81, 612 - 81);
                _output.WriteLine($"arrow chunk[0..150]: {arrowChunk.Substring(0, Math.Min(150, arrowChunk.Length))}");
                _output.WriteLine($"arrow chunk tail80: ...{arrowChunk.Substring(Math.Max(0, arrowChunk.Length - 80))}");
                // Increment from 0 (empty arrow body) up to full arrow chunk
                _output.WriteLine("--- minimal arrow+numeric-second reductions ---");
                Run(_output, "min_arrow_letInside_num2nd", "var m={k(){let s=()=>{let x=1};},918692(){return 1}};");
                Run(_output, "min_arrow_letInside_num2nd_nosemi", "var m={k(){let s=()=>{let x=1}},918692(){return 1}};");
                Run(_output, "min_arrow_simple_num2nd", "var m={k(){let s=()=>1},918692(){return 1}};");
                Run(_output, "min_let_num2nd", "var m={k(){let s=1},918692(){return 1}};");
                Run(_output, "min_letarrow_no_inner_let_num2nd", "var m={k(){let s=()=>{}},918692(){return 1}};");
                Run(_output, "min_letarrow_with_switch_num2nd", "var m={k(){let s=()=>{switch(1){case 1:return}}},918692(){return 1}};");
                Run(_output, "min_letarrow_with_tmpl_num2nd", "var m={k(){let s=()=>{`x ${1}`}},918692(){return 1}};");
                Run(_output, "min_letarrow_with_destruct_num2nd", "var m={k(){let s=()=>{let{x}=t}},918692(){return 1}};");
                Run(_output, "min_letarrow_switch_default_tmpl_num2nd", "var m={k(){let s=()=>{switch(1){default:(0,n.f)(`x ${1}`);return}}},918692(){return 1}};");

                // Just paste the actual arrow chunk into a minimal harness
                Run(_output, "exact_arrow_chunk_num2nd", "var m={k(){" + arrowChunk + "},918692(){return 1}};");
                _output.WriteLine($"arrow chunk: {arrowChunk}");

                // Strip the chunk progressively to find minimal trigger
                Run(_output, "r7_no_default",
                    "var m={k(){let s=(e,t)=>{let{type:r}=t;switch(t.type){case\"A\":{let{x:e}=t;return{g:{x:e}}}case\"B\":{let{y:r}=t;return{h:{y:r}}}}};},918692(){return 1}};");
                Run(_output, "r7_no_case_b",
                    "var m={k(){let s=(e,t)=>{let{type:r}=t;switch(t.type){case\"A\":{let{x:e}=t;return{g:{x:e}}}default:(0,n.f)(`x ${r}`);return}};},918692(){return 1}};");
                Run(_output, "r7_no_case_a",
                    "var m={k(){let s=(e,t)=>{let{type:r}=t;switch(t.type){case\"B\":{let{y:r}=t;return{h:{y:r}}}default:(0,n.f)(`x ${r}`);return}};},918692(){return 1}};");
                Run(_output, "r7_only_default",
                    "var m={k(){let s=(e,t)=>{let{type:r}=t;switch(t.type){default:(0,n.f)(`x ${r}`);return}};},918692(){return 1}};");
                Run(_output, "r7_no_switch",
                    "var m={k(){let s=(e,t)=>{let{type:r}=t;};},918692(){return 1}};");
                Run(_output, "r7_no_destructure",
                    "var m={k(){let s=(e,t)=>{switch(t.type){default:(0,n.f)(`x ${r}`);return}};},918692(){return 1}};");

                // Drill in: minimal default+return form
                Run(_output, "r8_default_return",
                    "var m={k(){let s=()=>{switch(t){default:return}};},918692(){return 1}};");
                Run(_output, "r8_default_return_no_arrow",
                    "var m={k(){switch(t){default:return}},918692(){return 1}};");
                Run(_output, "r8_default_return_in_fn",
                    "var m={k(){let s=function(){switch(t){default:return}};},918692(){return 1}};");
                Run(_output, "r8_default_return_outside",
                    "var m={k(){let s=()=>{};switch(t){default:return}},918692(){return 1}};");
                Run(_output, "r8_default_expr",
                    "var m={k(){let s=()=>{switch(t){default:a()}};},918692(){return 1}};");
                Run(_output, "r8_default_value",
                    "var m={k(){let s=()=>{switch(t){default:return 1}};},918692(){return 1}};");
                Run(_output, "r8_no_arrow_default_only",
                    "var m={k(){switch(t){default:return}},next(){return}};");
                Run(_output, "r8_arrow_default_only_ident2nd",
                    "var m={k(){let s=()=>{switch(t){default:return}};},next(){return 1}};");
                Run(_output, "r8_arrow_default_only_str2nd",
                    "var m={k(){let s=()=>{switch(t){default:return}};},'abc'(){return 1}};");

                // Bisect by REMOVING tail bytes from prevBody (keep full nextBody)
                _output.WriteLine("--- bisect prevBody by removing tail (full nextBody) ---");
                for (int trim = 0; trim <= 735; trim += 25)
                {
                    var pb = prevBody.Substring(0, Math.Max(0, prevBody.Length - trim));
                    var s = "var m={k(e,t,r){" + pb + "},next(e,t,r){" + nextBody + "}};";
                    var l6 = new Lexer(s); var p6 = new Parser(l6, false, allowRecovery: false);
                    try { p6.ParseProgram(); } catch { }
                    var ok = p6.Errors.Count == 0;
                    _output.WriteLine($"  trim {trim} (prev len {pb.Length}): {(ok ? "OK" : "FAIL")} — prevTail40 …{pb.Substring(Math.Max(0, pb.Length - 40))}");
                    if (ok) break;
                }

                // Drill into 918692 body: which part triggers when paired with simple prev?
                // Try progressively longer prefixes of nextBody combined with minimal prev.
                _output.WriteLine("--- bisect nextBody at depth-0 semicolons (with minimal prev=k(){}) ---");
                {
                    var sp2 = new System.Collections.Generic.List<int>();
                    int d = 0; char pv = '\0'; var ts = new System.Collections.Generic.Stack<int>();
                    for (int i = 0; i < nextBody.Length; i++)
                    {
                        char ch = nextBody[i];
                        if (ts.Count > 0)
                        {
                            if (ch == '{') { ts.Push(ts.Pop() + 1); d++; pv = ch; continue; }
                            if (ch == '}') { int top = ts.Pop(); if (top == 0) { i = SkipTemplateText(nextBody, i + 1, ts); pv = '`'; continue; } ts.Push(top - 1); d--; pv = ch; continue; }
                        }
                        switch (ch)
                        {
                            case '"': case '\'': i = SkipString(nextBody, i + 1, ch); pv = ch; break;
                            case '`': i = SkipTemplateText(nextBody, i + 1, ts); pv = '`'; break;
                            case '/':
                                if (i + 1 < nextBody.Length && nextBody[i+1] == '/') { while (i < nextBody.Length && nextBody[i] != '\n') i++; continue; }
                                if (i + 1 < nextBody.Length && nextBody[i+1] == '*') { i += 2; while (i + 1 < nextBody.Length && !(nextBody[i] == '*' && nextBody[i+1] == '/')) i++; i++; continue; }
                                if (IsRegexContext2(pv)) { i = SkipRegex(nextBody, i + 1); pv = '/'; continue; }
                                pv = ch; break;
                            case '{': d++; pv = ch; break;
                            case '}': d--; pv = ch; break;
                            case ';': if (d == 0) sp2.Add(i + 1); pv = ch; break;
                            default: if (!char.IsWhiteSpace(ch)) pv = ch; break;
                        }
                    }
                    _output.WriteLine($"nextBody depth-0 semis: {sp2.Count}");
                    int lastOk2 = 0, firstFail2 = -1;
                    foreach (var cut in sp2)
                    {
                        var nb = nextBody.Substring(0, cut);
                        var s2 = "var m={k(){},next(){" + nb + "}};";
                        var l4 = new Lexer(s2); var p4 = new Parser(l4, false, allowRecovery: false);
                        try { p4.ParseProgram(); } catch { }
                        var ok = p4.Errors.Count == 0;
                        if (ok) lastOk2 = cut;
                        else { firstFail2 = cut; _output.WriteLine($"  cut {cut}: FAIL — last 80: ...{nb.Substring(Math.Max(0, cut - 80))}"); break; }
                    }
                    if (firstFail2 < 0) _output.WriteLine($"  all cuts OK; lastOk={lastOk2}");
                    else _output.WriteLine($"  nextBody desync introduced by chars [{lastOk2}..{firstFail2}]");

                    // Beyond semicolons: bisect at `function` keyword boundaries (depth 0)
                    _output.WriteLine("--- bisecting nextBody at function declarations ---");
                    var fnStarts = new System.Collections.Generic.List<int>();
                    {
                        int d2 = 0; char pv2 = '\0'; var ts2 = new System.Collections.Generic.Stack<int>();
                        for (int i = 0; i < nextBody.Length - 8; i++)
                        {
                            char ch = nextBody[i];
                            if (ts2.Count > 0)
                            {
                                if (ch == '{') { ts2.Push(ts2.Pop() + 1); d2++; pv2 = ch; continue; }
                                if (ch == '}') { int top = ts2.Pop(); if (top == 0) { i = SkipTemplateText(nextBody, i + 1, ts2); pv2 = '`'; continue; } ts2.Push(top - 1); d2--; pv2 = ch; continue; }
                            }
                            switch (ch)
                            {
                                case '"': case '\'': i = SkipString(nextBody, i + 1, ch); pv2 = ch; break;
                                case '`': i = SkipTemplateText(nextBody, i + 1, ts2); pv2 = '`'; break;
                                case '/':
                                    if (i + 1 < nextBody.Length && nextBody[i+1] == '/') { while (i < nextBody.Length && nextBody[i] != '\n') i++; continue; }
                                    if (i + 1 < nextBody.Length && nextBody[i+1] == '*') { i += 2; while (i + 1 < nextBody.Length && !(nextBody[i] == '*' && nextBody[i+1] == '/')) i++; i++; continue; }
                                    if (IsRegexContext2(pv2)) { i = SkipRegex(nextBody, i + 1); pv2 = '/'; continue; }
                                    pv2 = ch; break;
                                case '{': d2++; pv2 = ch; break;
                                case '}': d2--; pv2 = ch; break;
                                default:
                                    if (d2 == 0 && i + 8 < nextBody.Length && string.CompareOrdinal(nextBody, i, "function", 0, 8) == 0)
                                    {
                                        fnStarts.Add(i);
                                    }
                                    if (!char.IsWhiteSpace(ch)) pv2 = ch;
                                    break;
                            }
                        }
                    }
                    _output.WriteLine($"function boundaries: {fnStarts.Count}");
                    foreach (var fs in fnStarts) _output.WriteLine($"  fn @ {fs}: {nextBody.Substring(fs, Math.Min(60, nextBody.Length - fs))}");
                    int prevEnd = lastOk2;
                    foreach (var fs in fnStarts)
                    {
                        if (fs <= prevEnd) continue;
                        // Find end of this function via balanced braces
                        int braceOpen = nextBody.IndexOf('{', fs);
                        if (braceOpen < 0) break;
                        int braceClose = FindMatchingBrace(nextBody, braceOpen);
                        if (braceClose < 0) break;
                        int endInclusive = braceClose + 1;
                        var nb = nextBody.Substring(0, endInclusive);
                        var s2 = "var m={k(){},next(){" + nb + "}};";
                        var l5 = new Lexer(s2); var p5 = new Parser(l5, false, allowRecovery: false);
                        try { p5.ParseProgram(); } catch { }
                        var ok = p5.Errors.Count == 0;
                        var head = nextBody.Substring(fs, Math.Min(80, endInclusive - fs));
                        _output.WriteLine($"  +fn @ {fs}: {(ok ? "OK" : "FAIL")} — {head}");
                        if (!ok) { _output.WriteLine($"    error: {p5.Errors[0]}"); break; }
                        prevEnd = endInclusive;
                    }
                }

                // Dump source around failure point
                var pairSrc = "var m={" + prev.id + "(e,t,r){" + prevBody + "}," + next.id + "(){return 1}};";
                _output.WriteLine($"pair_minnext source length: {pairSrc.Length}");
                _output.WriteLine($"chars [700..780]: {pairSrc.Substring(700, Math.Min(80, pairSrc.Length - 700))}");
                _output.WriteLine($"chars [750..830]: {pairSrc.Substring(750, Math.Min(80, pairSrc.Length - 750))}");
                {
                    // Try as shorthand method inside object literal
                    var shorthand = "var m={" + bad.id + "(e,t,r){" + body + "},next(){return 1}};";
                    var l2 = new Lexer(shorthand);
                    var p2 = new Parser(l2, false, allowRecovery: false);
                    try { p2.ParseProgram(); } catch { }
                    _output.WriteLine($"shorthand-method errors: {p2.Errors.Count}");
                    for (int i = 0; i < Math.Min(3, p2.Errors.Count); i++) _output.WriteLine($"  - {p2.Errors[i]}");
                    return;
                }
                if (p.Errors.Count > 0)
                {
                    for (int i = 0; i < Math.Min(3, p.Errors.Count); i++) _output.WriteLine($"  - {p.Errors[i]}");
                    // bisect by truncating body at safe semicolons
                    var semis = new System.Collections.Generic.List<int>();
                    for (int i = 0; i < body.Length; i++) if (body[i] == ';') semis.Add(i);
                    int firstBad = -1;
                    for (int idx = semis.Count - 1; idx >= 0; idx--)
                    {
                        var truncated = body.Substring(0, semis[idx] + 1);
                        var lex = new Lexer("(function(e,t,r){" + truncated + "})");
                        var pr = new Parser(lex, false, allowRecovery: false);
                        try { pr.ParseProgram(); } catch { }
                        if (pr.Errors.Count > 0) firstBad = idx;
                        else { _output.WriteLine($"OK up to semi[{idx}] at byte {semis[idx]}"); break; }
                    }
                    if (firstBad >= 0)
                    {
                        int s = firstBad > 0 ? semis[firstBad - 1] + 1 : 0;
                        int e = semis[firstBad] + 1;
                        _output.WriteLine($"first bad statement: bytes [{s}..{e}]");
                        _output.WriteLine(body.Substring(s, e - s));
                    }
                }
            }
        }

        // Wrap modules [0..k] as a module-map and try parsing. Bisect to find which
        // module index, when included, first causes a parse error.
        [Fact]
        public void Probe_BisectModuleSequence()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", "twitter_main.js");
            var src = File.ReadAllText(path);
            var entries = FindModuleEntries(src);
            _output.WriteLine($"total modules: {entries.Count}");

            string BuildPrefix(int upToInclusive)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("(function(){var __m={");
                for (int i = 0; i <= upToInclusive; i++)
                {
                    if (i > 0) sb.Append(',');
                    var e = entries[i];
                    sb.Append(e.id);
                    sb.Append("(e,t,r){");
                    sb.Append(src, e.bodyStart, e.bodyEnd - e.bodyStart);
                    sb.Append('}');
                }
                sb.Append("};})();");
                return sb.ToString();
            }

            bool ParseOk(int k)
            {
                var s = BuildPrefix(k);
                var l = new Lexer(s);
                var p = new Parser(l, false, allowRecovery: false);
                try { p.ParseProgram(); } catch { }
                return p.Errors.Count == 0;
            }

            // Linear scan in coarse chunks first
            int firstFail = -1;
            int step = 50;
            for (int k = step - 1; k < entries.Count; k += step)
            {
                if (!ParseOk(k)) { firstFail = k; break; }
            }
            _output.WriteLine($"coarse first-fail at module index ≤ {firstFail}");

            if (firstFail < 0) { _output.WriteLine("no failure found"); return; }

            // Binary search for exact module index
            int lo = Math.Max(0, firstFail - step), hi = firstFail;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (ParseOk(mid)) lo = mid + 1; else hi = mid;
            }
            _output.WriteLine($"exact first-failing module index: {lo}");
            var bad = entries[lo];
            _output.WriteLine($"module id={bad.id} body bytes={bad.bodyEnd - bad.bodyStart}");
            // Print body up to 600 chars
            var body = src.Substring(bad.bodyStart, Math.Min(600, bad.bodyEnd - bad.bodyStart));
            _output.WriteLine($"body[0..{body.Length}]:");
            _output.WriteLine(body);
        }

        private static System.Collections.Generic.List<(string id, int idStart, int bodyStart, int bodyEnd)> FindModuleEntries(string src)
        {
            var result = new System.Collections.Generic.List<(string, int, int, int)>();
            int n = src.Length;
            int depth = 0;
            char prev = '\0';
            var tmplStack = new System.Collections.Generic.Stack<int>();

            for (int i = 0; i < n; i++)
            {
                char c = src[i];

                if (tmplStack.Count > 0)
                {
                    if (c == '{') { tmplStack.Push(tmplStack.Pop() + 1); depth++; prev = c; continue; }
                    if (c == '}')
                    {
                        int top = tmplStack.Pop();
                        if (top == 0) { i = SkipTemplateText(src, i + 1, tmplStack); prev = '`'; continue; }
                        tmplStack.Push(top - 1); depth--; prev = c; continue;
                    }
                }

                switch (c)
                {
                    case '/':
                        if (i + 1 < n && src[i + 1] == '/') { while (i < n && src[i] != '\n') i++; continue; }
                        if (i + 1 < n && src[i + 1] == '*') { i += 2; while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/')) i++; i++; continue; }
                        if (IsRegexContext2(prev)) { i = SkipRegex(src, i + 1); prev = '/'; continue; }
                        prev = c; break;
                    case '"':
                    case '\'':
                        i = SkipString(src, i + 1, c); prev = c; break;
                    case '`':
                        i = SkipTemplateText(src, i + 1, tmplStack); prev = '`'; break;
                    case '{':
                        depth++; prev = c; break;
                    case '}':
                        depth--; prev = c; break;
                    case ',':
                        // Module map entries live exactly one level INSIDE the chunk map object.
                        // Webpack's outer wrapper looks like .push([[ids],{<map>},runtime]) so map's
                        // entries are at depth equal to the map's depth (varies). Instead we look
                        // for the pattern ",<digits>(" greedily.
                        if (i + 2 < n && char.IsDigit(src[i + 1]))
                        {
                            int j = i + 1;
                            while (j < n && char.IsDigit(src[j])) j++;
                            if (j < n && src[j] == '(' )
                            {
                                // Verify ")" then "{" follows somewhere reasonable.
                                int paren = 1;
                                int k = j + 1;
                                while (k < n && paren > 0)
                                {
                                    char ck = src[k];
                                    if (ck == '(') paren++;
                                    else if (ck == ')') paren--;
                                    else if (ck == '"' || ck == '\'') { k = SkipString(src, k + 1, ck); }
                                    k++;
                                }
                                if (k < n && src[k] == '{')
                                {
                                    // Find matching '}' for body using our balanced scan.
                                    int bStart = k + 1;
                                    int bEnd = FindMatchingBrace(src, k);
                                    if (bEnd > bStart)
                                    {
                                        var id = src.Substring(i + 1, j - i - 1);
                                        result.Add((id, i + 1, bStart, bEnd));
                                    }
                                }
                            }
                        }
                        prev = c; break;
                    default:
                        if (!char.IsWhiteSpace(c)) prev = c;
                        break;
                }
            }
            return result;
        }

        private static int FindMatchingBrace(string src, int openIdx)
        {
            int n = src.Length;
            int depth = 0;
            char prev = '\0';
            var tmpl = new System.Collections.Generic.Stack<int>();
            for (int i = openIdx; i < n; i++)
            {
                char c = src[i];
                if (tmpl.Count > 0)
                {
                    if (c == '{') { tmpl.Push(tmpl.Pop() + 1); depth++; prev = c; continue; }
                    if (c == '}')
                    {
                        int top = tmpl.Pop();
                        if (top == 0) { i = SkipTemplateText(src, i + 1, tmpl); prev = '`'; continue; }
                        tmpl.Push(top - 1); depth--; prev = c; continue;
                    }
                }
                switch (c)
                {
                    case '/':
                        if (i + 1 < n && src[i + 1] == '/') { while (i < n && src[i] != '\n') i++; continue; }
                        if (i + 1 < n && src[i + 1] == '*') { i += 2; while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/')) i++; i++; continue; }
                        if (IsRegexContext2(prev)) { i = SkipRegex(src, i + 1); prev = '/'; continue; }
                        prev = c; break;
                    case '"': case '\'':
                        i = SkipString(src, i + 1, c); prev = c; break;
                    case '`':
                        i = SkipTemplateText(src, i + 1, tmpl); prev = '`'; break;
                    case '{': depth++; prev = c; break;
                    case '}': depth--; if (depth == 0) return i; prev = c; break;
                    default: if (!char.IsWhiteSpace(c)) prev = c; break;
                }
            }
            return -1;
        }

        private static int SkipString(string src, int from, char q)
        {
            int n = src.Length;
            for (int i = from; i < n; i++)
            {
                if (src[i] == '\\') { i++; continue; }
                if (src[i] == q) return i;
                if (src[i] == '\n' || src[i] == '\r') return i;
            }
            return n - 1;
        }

        private static int SkipTemplateText(string src, int from, System.Collections.Generic.Stack<int> stack)
        {
            int n = src.Length;
            for (int i = from; i < n; i++)
            {
                if (src[i] == '\\') { i++; continue; }
                if (src[i] == '`') return i;
                if (src[i] == '$' && i + 1 < n && src[i + 1] == '{') { stack.Push(0); return i + 1; }
            }
            return n - 1;
        }

        private static int SkipRegex(string src, int from)
        {
            int n = src.Length;
            bool inClass = false;
            for (int i = from; i < n; i++)
            {
                char c = src[i];
                if (c == '\\') { i++; continue; }
                if (c == '[') inClass = true;
                else if (c == ']') inClass = false;
                else if (c == '/' && !inClass) { i++; while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++; return i - 1; }
                else if (c == '\n' || c == '\r') return i;
            }
            return n - 1;
        }

        private static bool IsRegexContext2(char prev)
        {
            if (prev == '\0') return true;
            if (char.IsLetterOrDigit(prev) || prev == '_' || prev == '$') return false;
            if (prev == ')' || prev == ']' || prev == '`' || prev == '"' || prev == '\'') return false;
            return true;
        }

        [Fact]
        public void Probe_FindBundleDesyncPoint()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", "twitter_main.js");
            var src = File.ReadAllText(path);

            // Slice around col 674972 to a self-contained `!function(){...}()` wrapper.
            // The bundle is a single IIFE; we can't slice mid-stream, so just try
            // wrapping a chunk in `!function(){ <slice> ;}()` and report errors.
            int center = 674972;
            int radius = 8000;
            int start = Math.Max(0, center - radius);
            int end = Math.Min(src.Length, center + radius);
            var slice = src.Substring(start, end - start);
            _output.WriteLine($"slice [{start}..{end}] len={slice.Length}");

            var lexer = new Lexer(slice);
            var parser = new Parser(lexer, false, allowRecovery: false);
            try { parser.ParseProgram(); } catch { }
            _output.WriteLine($"slice errors: {parser.Errors.Count}");
            for (int i = 0; i < Math.Min(parser.Errors.Count, 5); i++)
            {
                _output.WriteLine($"  - {parser.Errors[i]}");
            }
        }

        [Fact]
        public void DumpTokens_MinimalFailing()
        {
            var src = "var r=()=>{`${1}`;return},l=1;";
            _output.WriteLine($"source: {src}");
            var lexer = new Lexer(src);
            for (int i = 0; i < 40; i++)
            {
                var t = lexer.NextToken();
                _output.WriteLine($"  {i,2}: {t.Type,-20} '{t.Literal}' @ line {t.Line} col {t.Column}");
                if (t.Type == TokenType.Eof) break;
            }
        }

        [Fact]
        public void Probe_DumpExceptionInstallationChain()
        {
            // Google's xjs installs _DumpException via the gbar namespace pattern.
            // The error chain we saw: trace _ → _s → _DumpException → ... → _DumpException
            // suggests _._DumpException is being read but is undefined when called.
            // Walk the four steps the user listed: declared, assigned, written, looked up.
            string Run(string js)
            {
                var lex = new Lexer(js);
                var par = new Parser(lex, false);
                var ast = par.ParseProgram();
                var compiler = new FenBrowser.FenEngine.Core.Bytecode.Compiler.BytecodeCompiler();
                var env = FenRuntime.CreateStandaloneIntrinsicScope();
                var vm = new FenBrowser.FenEngine.Core.Bytecode.VM.VirtualMachine();
                var block = compiler.Compile(ast);
                return vm.Execute(block, env).ToString();
            }

            _output.WriteLine($"top-level typeof this = {Run("typeof this")}");
            _output.WriteLine($"top-level this===undefined? {Run("String(this===undefined)")}");
            _output.WriteLine($"top-level this===globalThis? {Run("String(this===globalThis)")}");

            // 1. Function declared as a function statement, then assigned onto namespace
            Assert.Equal("ok", Run(
                "function _DumpException(e){return 'ok:'+e}\n" +
                "var _={}; _._DumpException = _DumpException;\n" +
                "typeof _._DumpException === 'function' ? 'ok' : 'BAD: ' + typeof _._DumpException"));

            // 2. Function expression assigned directly
            Assert.Equal("ok", Run(
                "var _={}; _._DumpException = function(e){return 'd:'+e};\n" +
                "typeof _._DumpException === 'function' ? 'ok' : 'BAD: ' + typeof _._DumpException"));

            // 2b. Chained assignment: c = c[d] = {} (used by Closure path walker)
            Assert.Equal("ok", Run(
                "var root={}, c=root, d='x'; c=c[d]={}; c.inner='ok'; root.x.inner"));

            // 3. Common Closure pattern: this-assignment with var rebinding
            Assert.Equal("d:x", Run(
                "var _=this.gbar_=this.gbar_||{};\n" +
                "_._DumpException = function(e){return 'd:'+e};\n" +
                "_._DumpException('x')"));

            // 4. Across script boundaries: declare in one statement-list, lookup in another
            //    (simulates two <script> tags or sequential script execution)
            Assert.Equal("d:y", Run(
                "this._DumpException = function(e){return 'd:'+e};\n" +
                "this._DumpException('y')"));

            // 5. The actual failure pattern: accessing on a nested namespace
            //    that hasn't been initialized
            Assert.Equal("undefined", Run(
                "var _={}; typeof _._DumpException"));

            _output.WriteLine("All _DumpException install patterns work.");

            // Verify `this` inside IIFE called via .call(this)
            _output.WriteLine("IIFE this: " + Run("(function(){return typeof this}).call(this)"));
            // Verify IIFE assignment to `_.u`
            _output.WriteLine("IIFE _.u: " + Run("(function(_){_.u = this || self; return typeof _.u}).call({}, {})"));

            // Step-by-step minimal Md repro
            _output.WriteLine("md step1: " + Run("var ns={}; ns.gbar_=ns.gbar_||{}; typeof ns.gbar_"));
            _output.WriteLine("md step2: " + Run("var ns={}; ns.gbar_=ns.gbar_||{}; ns.gbar_.x=1; ns.gbar_.x"));
            // Critical: this.X at top level must make X a bare global
            _output.WriteLine("this.X->bare: " + Run("this.foo1 = 42; typeof foo1"));
            _output.WriteLine("globalThis.X->bare: " + Run("globalThis.foo2 = 42; typeof foo2"));
            _output.WriteLine("var X->this.X: " + Run("var foo3 = 42; String(this.foo3)"));
            // Path walker with hardcoded path
            _output.WriteLine("md wrapped: " + Run(@"
                var ns = {};
                ns.gbar_ = ns.gbar_ || {};
                var u = ns;
                var Md = function(a, b) {
                    a = a.split('.');
                    for (var c = u, d; a.length && (d = a.shift());)
                        a.length || b === void 0
                          ? c[d] && c[d] !== Object.prototype[d] ? c = c[d] : c = c[d] = {}
                          : c[d] = b;
                };
                Md('gbar_._DumpException', function(e){return 'ok:'+e});
                typeof ns.gbar_._DumpException
            "));

            _output.WriteLine("md walker: " + Run(@"
                var ns = {};
                ns.gbar_ = ns.gbar_ || {};
                var u = ns;
                var b = function(e){return 'ok:'+e};
                var a = ['gbar_', '_DumpException'];
                var c = u, d;
                while (a.length && (d = a.shift())) {
                    if (a.length || b === void 0) {
                        if (c[d] && c[d] !== Object.prototype[d]) c = c[d];
                        else c = c[d] = {};
                    } else {
                        c[d] = b;
                    }
                }
                typeof ns.gbar_._DumpException
            "));

            // Critical sub-piece: top-level `this` is the global object in non-strict mode
            Assert.Equal("true", Run(@"
                var x = this || self;
                this.testProp = 42;
                String(x.testProp === 42)
            "));
            Assert.Equal("object:object:object", Run(@"
                this.gbar_ = this.gbar_ || {};
                (function(_){
                    _.u = this || self;
                }).call(this, {});
                var probe = {};
                (function(_){
                    _.u = this || self;
                }).call(this, probe);
                typeof probe.u + ':' + typeof this.gbar_ + ':' + typeof gbar_
            "));
            // Chained assignment: c = c[d] = {} should set c[d] AND make c point to it
            Assert.Equal("ok", Run(@"
                var root = {};
                var c = root, d = 'x';
                c = c[d] = {};
                c.inner = 'ok';
                root.x.inner
            "));
            // EXACT gbar invocation: })(this.gbar_) — not .call(this)
            _output.WriteLine("gbar EXACT: " + Run(@"
                this.gbar_ = this.gbar_ || {};
                (function(_){
                    _.u = this || self;
                    _.Md = function(a,b){
                        a = a.split('.');
                        for (var c=_.u, d; a.length && (d=a.shift());)
                            a.length || b === void 0
                              ? c[d] && c[d] !== Object.prototype[d] ? c = c[d] : c = c[d] = {}
                              : c[d] = b;
                    };
                    _.Md('gbar_._DumpException', function(e){ return 'ok:'+e; });
                })(this.gbar_);
                typeof this.gbar_._DumpException + ':' + this.gbar_._DumpException('hi')
            "));

            // Ternary with chained assignment (the exact gbar Md pattern)
            Assert.Equal("done", Run(@"
                var root = {};
                var c = root, d = 'x';
                var a = ['x'], b = 'done';
                a.length || b === void 0
                  ? c[d] && c[d] !== Object.prototype[d] ? c = c[d] : c = c[d] = {}
                  : c[d] = b;
                d = a.shift();
                c[d] = b;
                root.x.x
            "));

            // Pattern actually used by Google's gbar bundle: walks a dotted
            // path string and assigns at the leaf. Test the exact form.
            Assert.Equal("ok:", Run(@"
                this.gbar_ = this.gbar_ || {};
                (function(){
                    var _ = this.gbar_ = this.gbar_ || {};
                    _.u = this || self;
                    _.Md = function(a,b){
                        a = a.split('.');
                        for (var c=_.u, d; a.length && (d=a.shift());)
                            a.length || b === void 0
                              ? c[d] && c[d] !== Object.prototype[d] ? c = c[d] : c = c[d] = {}
                              : c[d] = b;
                    };
                    _.Md('gbar_._DumpException', function(e){ return 'ok:'+e; });
                }).call(this);
                typeof gbar_._DumpException === 'function' ? gbar_._DumpException('') : 'BAD:' + typeof gbar_._DumpException
            "));
        }

        [Fact]
        public void Probe_CapturedLetFastSlotResolution()
        {
            string Run(string js)
            {
                var lex = new Lexer(js);
                var par = new Parser(lex, false);
                var ast = par.ParseProgram();
                var compiler = new FenBrowser.FenEngine.Core.Bytecode.Compiler.BytecodeCompiler();
                var env = FenRuntime.CreateStandaloneIntrinsicScope();
                var vm = new FenBrowser.FenEngine.Core.Bytecode.VM.VirtualMachine();
                var block = compiler.Compile(ast);
                return vm.Execute(block, env).ToString();
            }

            Assert.Equal("ok", Run(@"
                function outer(){
                    var pad0, pad1, pad2, pad3, pad4, pad5, pad6, pad7, pad8, pad9;
                    let e = { i: 'ok' };
                    {
                        var inner = function(){ return e.i; };
                        return inner();
                    }
                }
                outer()
            "));

            Assert.Equal("ok", Run(@"
                function outer(){
                    for (let e of [{ i: 'ok' }]) {
                        let f = 1;
                        var inner = function(){ return e.i; };
                        return inner();
                    }
                    return 'bad';
                }
                outer()
            "));
        }

        [Fact]
        public void Probe_FunctionPrototypeCall()
        {
            // Verify that .call / .apply / .bind work on both native and user
            // functions, with proper this-binding. webpack and React lean on
            // these constantly (Function.prototype.call.bind(...), etc.).
            string Run(string js)
            {
                var lex = new Lexer(js);
                var par = new Parser(lex, false);
                var ast = par.ParseProgram();
                var compiler = new FenBrowser.FenEngine.Core.Bytecode.Compiler.BytecodeCompiler();
                var env = FenRuntime.CreateStandaloneIntrinsicScope();
                var vm = new FenBrowser.FenEngine.Core.Bytecode.VM.VirtualMachine();
                var block = compiler.Compile(ast);
                var result = vm.Execute(block, env);
                return result.ToString();
            }

            // User function: .call with this and args
            Assert.Equal("hi-x-y", Run("function f(a,b){return this+'-'+a+'-'+b}; f.call('hi','x','y')"));
            // User function: .apply with this and array args
            Assert.Equal("hi-x-y", Run("function f(a,b){return this+'-'+a+'-'+b}; f.apply('hi',['x','y'])"));
            // User function: .bind partial application
            Assert.Equal("hi-x-y", Run("function f(a,b){return this+'-'+a+'-'+b}; var g=f.bind('hi','x'); g('y')"));
            // Native function: .call exists on String.prototype.toUpperCase etc.
            Assert.Equal("HI", Run("String.prototype.toUpperCase.call('hi')"));
            // Native + this binding
            Assert.Equal("1,2,3", Run("Array.prototype.join.call([1,2,3], ',')"));
            // Method call this binding
            Assert.Equal("hi-x-y", Run("var o={s:'hi',f:function(a,b){return this.s+'-'+a+'-'+b}}; o.f('x','y')"));
            // webpack-style (0, fn)(args) in strict mode → this=undefined
            Assert.Equal("undefined", Run("'use strict'; function f(){return typeof this}; (0,f)()"));
            // non-strict (0, fn)() → this=global (object)
            Assert.Equal("object", Run("function f(){return typeof this}; (0,f)()"));
            _output.WriteLine("All Function.prototype.call/apply/bind cases passed.");
        }

        [Fact]
        public void Probe_NumericLiteralExponentLookup()
        {
            // x.com's vendor bundle does: r(973e3) — scientific notation that JS
            // parses as 973000. The module is registered as {973000: function...}.
            // Our LoadProp trace showed `973000` as the failing key, meaning either:
            //   1. Our lexer/parser turns 973e3 into something other than 973000, or
            //   2. Object[973e3] lookup mismatches the registered key "973000".
            // This probe confirms both forms work and round-trip equivalently.
            var lexer = new Lexer("var m={973000:'hit'}; var a=m[973e3]; var b=m[973000]; var c=String(973e3); var d=(973e3===973000);");
            var parser = new Parser(lexer, false, allowRecovery: false);
            var ast = parser.ParseProgram();
            _output.WriteLine($"parse errors: {parser.Errors.Count}");
            foreach (var e in parser.Errors) _output.WriteLine("  " + e);

            var vm = new FenBrowser.FenEngine.Core.Bytecode.VM.VirtualMachine();
            var compiler = new FenBrowser.FenEngine.Core.Bytecode.Compiler.BytecodeCompiler();
            var env = FenRuntime.CreateStandaloneIntrinsicScope();
            var codeBlock = compiler.Compile(ast);
            vm.Execute(codeBlock, env);

            string Eval(string js)
            {
                var l = new Lexer(js); var p = new Parser(l, false);
                var a2 = p.ParseProgram();
                var b2 = compiler.Compile(a2);
                var v = vm.Execute(b2, env);
                return v.ToString();
            }
            _output.WriteLine($"a (m[973e3]):     {Eval("a")}");
            _output.WriteLine($"b (m[973000]):    {Eval("b")}");
            _output.WriteLine($"c (String(973e3)): {Eval("c")}");
            _output.WriteLine($"d (973e3===973000): {Eval("d")}");
            Assert.Equal("hit", Eval("a"));
            Assert.Equal("hit", Eval("b"));

            // The actual webpack pattern uses scientific notation as the SHORTHAND
            // METHOD NAME inside an object literal. That's the real test:
            var lex2 = new Lexer("var m={973e3(e){return 'shorthand-hit:'+e}}; var r=m[973000](42);");
            var par2 = new Parser(lex2, false, allowRecovery: false);
            var ast2 = par2.ParseProgram();
            _output.WriteLine($"shorthand-exp parse errors: {par2.Errors.Count}");
            foreach (var e in par2.Errors) _output.WriteLine("  " + e);
            if (par2.Errors.Count == 0)
            {
                var b3 = compiler.Compile(ast2);
                vm.Execute(b3, env);
                _output.WriteLine($"r = {Eval("r")}");
                Assert.Equal("shorthand-hit:42", Eval("r"));
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Probe_DynamicScriptInjection()
        {
            // Reproduces webpack's lazy-chunk loading path: JS creates a <script>,
            // sets src, appends to head, and expects an onload event after the URL
            // fetches and executes. If this hangs / never fires onload, webpack's
            // n.e(chunkId) Promise never resolves and React Suspense never renders.
            EngineContext.Reset();
            FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.ResetInstance();
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><head></head><body><script>\n" +
                "window.__loaded = false;\n" +
                "window.__error = false;\n" +
                "var s = document.createElement('script');\n" +
                "s.src = 'https://example.com/chunk.js';\n" +
                "s.onload = function() { window.__loaded = true; };\n" +
                "s.onerror = function() { window.__error = true; };\n" +
                "document.head.appendChild(s);\n" +
                "</script></body></html>",
                baseUri);
            var doc = parser.Parse();
            var engine = new JavaScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }));
            engine.AllowExternalScripts = true;
            // Provide an in-memory fetch so the test is hermetic
            engine.FetchOverride = (uri) => System.Threading.Tasks.Task.FromResult("window.__chunkRan = true;");
            await engine.SetDomAsync(doc.DocumentElement, baseUri);
            // Drain the event loop a few ticks so the detached async fetch + scheduled
            // task have a chance to complete.
            for (int i = 0; i < 10; i++)
            {
                FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance.ProcessNextTask();
                await System.Threading.Tasks.Task.Delay(50);
            }
            var loaded = engine.Evaluate("window.__loaded")?.ToString();
            var chunkRan = engine.Evaluate("window.__chunkRan")?.ToString();
            var errored = engine.Evaluate("window.__error")?.ToString();
            _output.WriteLine($"loaded={loaded}, chunkRan={chunkRan}, errored={errored}");
            Assert.Equal("true", (chunkRan ?? "").ToLowerInvariant()); // The injected chunk JS must execute
            Assert.Equal("true", (loaded ?? "").ToLowerInvariant());   // The onload event must fire
        }

        [Fact]
        public void Probe_AllFourBundlesParse()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "TestData");
            foreach (var file in new[] { "twitter_vendor.js", "twitter_i18n_en.js", "twitter_loggedout.js", "twitter_loggedouthome.js", "twitter_loggedoutroutes.js", "twitter_main.js" })
            {
                var path = Path.Combine(dir, file);
                if (!File.Exists(path)) { _output.WriteLine($"{file}: MISSING"); continue; }
                var src = File.ReadAllText(path);
                var lex = new Lexer(src);
                var par = new Parser(lex, false, allowRecovery: false);
                try { par.ParseProgram(); } catch { }
                _output.WriteLine($"{file} ({src.Length} bytes): {par.Errors.Count} error(s)");
                for (int i = 0; i < Math.Min(3, par.Errors.Count); i++)
                {
                    var e = par.Errors[i];
                    if (e.Length > 200) e = e.Substring(0, 200) + "...";
                    _output.WriteLine($"  - {e}");
                }
            }
        }

        [Fact]
        public void Probe_FirstParseError()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", "twitter_main.js");
            Assert.True(File.Exists(path), $"missing test data: {path}");
            var src = File.ReadAllText(path);

            var lexer = new Lexer(src);
            var parser = new Parser(lexer, isModule: false, allowRecovery: false);
            try { parser.ParseProgram(); }
            catch (Exception ex)
            {
                _output.WriteLine($"ParseProgram threw: {ex.GetType().Name}: {ex.Message}");
            }

            var errors = parser.Errors;
            _output.WriteLine($"Total errors: {errors.Count}");
            for (int i = 0; i < Math.Min(errors.Count, 5); i++)
            {
                _output.WriteLine($"--- error {i} ---");
                _output.WriteLine(errors[i]);
            }

            // Show byte window around the first reported column (which appears to
            // be a global byte offset, not a per-line column).
            var first = errors.Count > 0 ? errors[0] : "";
            var colIdx = first.IndexOf("column ", StringComparison.Ordinal);
            if (colIdx >= 0)
            {
                var rest = first.Substring(colIdx + "column ".Length);
                var num = new string(System.Linq.Enumerable.TakeWhile(rest, char.IsDigit).ToArray());
                if (int.TryParse(num, out var col) && col > 0 && col <= src.Length)
                {
                    var start = Math.Max(0, col - 120);
                    var end = Math.Min(src.Length, col + 80);
                    _output.WriteLine($"--- src[{start}..{end}] (1-indexed col {col}) ---");
                    _output.WriteLine(src.Substring(start, end - start));
                    _output.WriteLine($"--- char at col {col}: {(int)src[col - 1]} '{src[col - 1]}' ---");
                }
            }
        }
    }
}
