using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML §4.10.20 constraint validation. x.com's login form reads
/// <c>input.validity.valid</c> when Continue is pressed; with no ValidityState
/// behind the published name it threw "Cannot read properties of undefined".
/// </summary>
public sealed class ConstraintValidationTests
{
    [Fact]
    public async Task ARequiredEmptyInputIsMissingItsValueAndBecomesValidOnceFilled()
    {
        var engine = await CreateEngineAsync("<input id=i required>");
        var result = engine.Evaluate(
            "(function () {" +
            "  var i = document.getElementById('i'), v = i.validity, before = [v.valid, v.valueMissing, i.validationMessage !== ''];" +
            "  i.value = 'aukr1';" +
            "  return before.concat([v.valid, v.valueMissing, i.validity === v, v instanceof ValidityState]).join(',');" +
            "})()")?.ToString();

        Assert.Equal("false,true,true,true,false,true,true", result);
    }

    [Fact]
    public async Task EmailPatternAndLengthConstraintsAreChecked()
    {
        var engine = await CreateEngineAsync(
            "<input id=e type=email value=x><input id=p pattern='[a-z]+[0-9]'><input id=l maxlength=3 minlength=2>");
        var result = engine.Evaluate(
            "(function () {" +
            "  var e = document.getElementById('e'), p = document.getElementById('p'), l = document.getElementById('l');" +
            "  var out = [e.validity.typeMismatch];" +
            "  e.value = 'a@b.co'; out.push(e.validity.typeMismatch);" +
            "  p.value = 'aukr1'; out.push(p.validity.patternMismatch);" +
            "  p.value = 'AUKR'; out.push(p.validity.patternMismatch);" +
            "  l.value = 'abcd'; out.push(l.validity.tooLong);" +
            "  l.value = 'a'; out.push(l.validity.tooShort);" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal("true,false,false,true,true,true", result);
    }

    [Fact]
    public async Task NumberRangeAndStepConstraintsAreChecked()
    {
        var engine = await CreateEngineAsync("<input id=n type=number min=2 max=10 step=2>");
        var result = engine.Evaluate(
            "(function () {" +
            "  var n = document.getElementById('n'), out = [];" +
            "  n.value = '0'; out.push(n.validity.rangeUnderflow);" +
            "  n.value = '12'; out.push(n.validity.rangeOverflow);" +
            "  n.value = '5'; out.push(n.validity.stepMismatch);" +
            "  n.value = '6'; out.push(n.validity.valid);" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal("true,true,true,true", result);
    }

    [Fact]
    public async Task CustomValidityAndCheckValidityFireInvalid()
    {
        var engine = await CreateEngineAsync("<form id=f><input id=i></form>");
        var result = engine.Evaluate(
            "(function () {" +
            "  var i = document.getElementById('i'), f = document.getElementById('f'), fired = 0;" +
            "  i.addEventListener('invalid', function (e) { if (e.cancelable && !e.bubbles) fired++; });" +
            "  var out = [i.checkValidity()];" +
            "  i.setCustomValidity('taken');" +
            "  out.push(i.validity.customError, i.validationMessage, i.checkValidity(), f.checkValidity(), fired);" +
            "  i.setCustomValidity('');" +
            "  out.push(i.validity.valid, f.reportValidity());" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal("true,true,taken,false,false,2,true,true", result);
    }

    [Fact]
    public async Task BarredElementsDoNotValidate()
    {
        var engine = await CreateEngineAsync(
            "<input id=h type=hidden required><input id=d required disabled><input id=r required readonly>" +
            "<fieldset disabled><input id=fs required></fieldset><button id=b type=button></button><input id=ok required>");
        var result = engine.Evaluate(
            "['h','d','r','fs','b','ok'].map(function (id) {" +
            "  var el = document.getElementById(id); return el.willValidate + ':' + el.validity.valid;" +
            "}).join(',')")?.ToString();

        Assert.Equal("false:true,false:true,false:true,false:true,false:true,true:false", result);
    }

    [Fact]
    public async Task ARequiredRadioGroupNeedsOneChecked()
    {
        var engine = await CreateEngineAsync(
            "<form><input type=radio name=g id=a required><input type=radio name=g id=b></form>");
        var result = engine.Evaluate(
            "(function () {" +
            "  var a = document.getElementById('a'), b = document.getElementById('b');" +
            "  var out = [a.validity.valueMissing, b.validity.valueMissing];" +
            "  b.checked = true;" +
            "  out.push(a.validity.valueMissing, b.validity.valueMissing);" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal("true,true,false,false", result);
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string body)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body>" + body + "</body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
