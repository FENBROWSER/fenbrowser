using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSSOM 6.4 rule interfaces: every at-rule a sheet holds is reported with its own
/// interface, attributes live on the prototypes, and grouping rules take
/// insertRule/deleteRule.
/// </summary>
[Collection("Engine Tests")]
public sealed class CssomRuleInterfaceTests
{
    private static async Task<string?> RunAsync(string script)
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync("data:text/html,<div id='x'></div>"));
            var result = await browser.ExecuteScriptAsync(script);
            return result?.ToString();
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task EveryRuleKindHasItsInterfaceAndType()
    {
        var result = await RunAsync(@"
            (function () {
                try {
                var s = document.createElement('style');
                s.textContent = '@import url(""data:text/css,"");\n' +
                    '@namespace url(""http://www.w3.org/1999/xhtml"");\n' +
                    'div { color: green }\n' +
                    '@media screen { div { color: red } }\n' +
                    '@supports (color: green) { div { color: blue } }\n' +
                    '@font-face { font-family: test }\n' +
                    '@page { margin: 1cm }\n' +
                    '@keyframes test { from { opacity: 0 } to { opacity: 1 } }\n' +
                    '@counter-style thumbs { system: cyclic; symbols: x }\n' +
                    '@layer foo { div { color: purple } }\n' +
                    '@layer bar;\n' +
                    '@container (min-width: 0px) { div { color: orange } }\n';
                document.head.appendChild(s);
                var rules = s.sheet.cssRules;
                var out = [rules.length, rules instanceof CSSRuleList];
                for (var i = 0; i < rules.length; i++) {
                    out.push(Object.prototype.toString.call(rules[i]).slice(8, -1) + ':' + rules[i].type);
                }
                out.push(rules[3].cssText);
                out.push(rules[7].cssText);
                out.push(rules[1].cssText);
                return out.join('|');
                } catch (e) { return 'ERR ' + e; }
            })();");

        Assert.Equal(
            "12|true|CSSImportRule:3|CSSNamespaceRule:10|CSSStyleRule:1|CSSMediaRule:4|CSSSupportsRule:12|" +
            "CSSFontFaceRule:5|CSSPageRule:6|CSSKeyframesRule:7|CSSCounterStyleRule:11|CSSLayerBlockRule:0|" +
            "CSSLayerStatementRule:0|CSSContainerRule:0|" +
            "@media screen {\n  div { color: red; }\n}|" +
            "@keyframes test {\n  0% { opacity: 0; }\n  100% { opacity: 1; }\n}|" +
            "@namespace url(\"http://www.w3.org/1999/xhtml\");",
            result);
    }

    [Fact]
    public async Task RuleAttributesAreReadOnlyPrototypeAccessors()
    {
        var result = await RunAsync(@"
            (function () {
                try {
                var s = document.createElement('style');
                s.textContent = 'div { margin: 10px; }';
                document.head.appendChild(s);
                var rule = s.sheet.cssRules[0];
                var out = [];
                out.push(!rule.hasOwnProperty('cssText') && 'cssText' in rule);
                out.push(Object.getOwnPropertyDescriptor(CSSRule.prototype, 'type').set === undefined);
                rule.type = 5;
                out.push(rule.type === 1 && rule.STYLE_RULE === 1 && CSSRule.NAMESPACE_RULE === 10);
                out.push(rule.parentRule === null && rule.parentStyleSheet === s.sheet);
                var style = rule.style;
                rule.style = 'margin: 42px;';
                out.push(rule.style === style && rule.style.margin === '42px');
                return out.join(',');
                } catch (e) { return 'ERR ' + e; }
            })();");

        Assert.Equal("true,true,true,true,true", result);
    }

    [Fact]
    public async Task GroupingAndKeyframesRulesAreEditable()
    {
        var result = await RunAsync(@"
            (function () {
                try {
                var s = document.createElement('style');
                s.textContent = '@media all { * {} } @keyframes foo { 0% { top: 0px; } 100% { top: 200px; } }';
                document.head.appendChild(s);
                var media = s.sheet.cssRules[0], keyframes = s.sheet.cssRules[1], out = [];
                out.push(media.insertRule('.foo {}', 0));
                out.push(media.cssRules.length + ':' + media.cssRules[0].selectorText + ':' + (media.cssRules[0].parentRule === media));
                try { media.insertRule('@import url(""x.css"");', 0); out.push('no-throw'); } catch (e) { out.push(e.name); }
                try { media.insertRule('.a {}', 9); out.push('no-throw'); } catch (e) { out.push(e.name); }
                media.deleteRule(0);
                out.push(media.cssRules.length);
                keyframes.appendRule('50% { top: 100px; }');
                out.push(keyframes.length + ':' + keyframes[2].keyText + ':' + keyframes.findRule('50%').cssText);
                keyframes.deleteRule('100%');
                out.push(keyframes.cssRules.length + ':' + (keyframes[2] === undefined));
                try { s.sheet.insertRule('@import url(""x.css"");', 1); out.push('no-throw'); } catch (e) { out.push(e.name); }
                return out.join(',');
                } catch (e) { return 'ERR ' + e; }
            })();");

        Assert.Equal(
            "0,2:.foo:true,HierarchyRequestError,IndexSizeError,1,3:50%:50% { top: 100px; },2:true,HierarchyRequestError",
            result);
    }
}
