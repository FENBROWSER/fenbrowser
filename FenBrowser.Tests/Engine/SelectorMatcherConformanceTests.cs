using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class SelectorMatcherConformanceTests
    {
        [Fact]
        public void NthChild_WithOfSelector_MatchesFilteredSiblingSet()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <ul>
        <li id='a' class='keep'>a</li>
        <li id='b'>b</li>
        <li id='c' class='keep'>c</li>
        <li id='d' class='keep'>d</li>
    </ul>
</body></html>");

            var a = ById(doc, "a");
            var c = ById(doc, "c");
            var d = ById(doc, "d");

            Assert.True(SelectorMatcher.Matches(c, "li:nth-child(2 of .keep)"));
            Assert.False(SelectorMatcher.Matches(a, "li:nth-child(2 of .keep)"));
            Assert.False(SelectorMatcher.Matches(d, "li:nth-child(2 of .keep)"));
            Assert.True(SelectorMatcher.Matches(d, "li:nth-last-child(1 of .keep)"));
        }

        [Fact]
        public void AttributeSelectors_SupportCaseFlagsAndBracketInQuotedValue()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='target' data-code='AbC' data-note='A ] B'></div>
</body></html>");

            var target = ById(doc, "target");

            Assert.True(SelectorMatcher.Matches(target, "[data-code='abc' i]"));
            Assert.False(SelectorMatcher.Matches(target, "[data-code='abc' s]"));
            Assert.True(SelectorMatcher.Matches(target, "[data-code='AbC' s]"));
            Assert.True(SelectorMatcher.Matches(target, "[data-note='a ] b' i]"));
        }

        [Fact]
        public void AttributeSelectors_UnescapeEscapedWhitespaceInUnquotedValue()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <address id='target' class='second two'></address>
</body></html>");

            var target = ById(doc, "target");

            Assert.True(SelectorMatcher.Matches(target, @"[class=second\ two]"));
            Assert.True(SelectorMatcher.Matches(target, @"[class=second\ two][class=""second two""]"));
        }

        [Fact]
        public void EmptyPseudoClass_IgnoresComments_ButNotWhitespaceText()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='comment-only'><!--comment--></div>
    <div id='whitespace'> </div>
    <div id='empty'></div>
</body></html>");

            var commentOnly = ById(doc, "comment-only");
            var whitespace = ById(doc, "whitespace");
            var empty = ById(doc, "empty");

            Assert.True(SelectorMatcher.Matches(commentOnly, ":empty"));
            Assert.False(SelectorMatcher.Matches(whitespace, ":empty"));
            Assert.True(SelectorMatcher.Matches(empty, ":empty"));
        }

        [Fact]
        public void HasPseudoClass_SupportsRelativeCombinators()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='child-host'><img alt='x' /></div>
    <div id='child-miss'><span></span></div>

    <div id='adjacent-host'></div><div class='next-hit'></div>
    <div id='sibling-host'></div><div></div><div class='later-hit'></div>

    <section id='nested-host'>
        <article class='card'><span class='leaf'></span></article>
    </section>
    <section id='nested-miss'>
        <article class='card'></article>
    </section>
</body></html>");

            var childHost = ById(doc, "child-host");
            var childMiss = ById(doc, "child-miss");
            var adjacentHost = ById(doc, "adjacent-host");
            var siblingHost = ById(doc, "sibling-host");
            var nestedHost = ById(doc, "nested-host");
            var nestedMiss = ById(doc, "nested-miss");

            Assert.True(SelectorMatcher.Matches(childHost, "div:has(> img)"));
            Assert.False(SelectorMatcher.Matches(childMiss, "div:has(> img)"));
            Assert.True(SelectorMatcher.Matches(adjacentHost, "div:has(+ .next-hit)"));
            Assert.True(SelectorMatcher.Matches(siblingHost, "div:has(~ .later-hit)"));
            Assert.True(SelectorMatcher.Matches(nestedHost, "section:has(> article .leaf)"));
            Assert.False(SelectorMatcher.Matches(nestedMiss, "section:has(> article .leaf)"));
        }

        [Fact]
        public void IsWhereAndNestedNotSelectors_MatchPerSelectorsLevel4Semantics()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <button id='probe' class='primary action'>Go</button>
</body></html>");

            var probe = ById(doc, "probe");

            Assert.True(SelectorMatcher.Matches(probe, "button:is(.ghost, .primary)"));
            Assert.True(SelectorMatcher.Matches(probe, "button:where(.primary, .ghost)"));
            Assert.False(SelectorMatcher.Matches(probe, "button:is(.ghost, .secondary)"));
            Assert.True(SelectorMatcher.Matches(probe, "button:not(:is(.ghost, .secondary))"));
            Assert.False(SelectorMatcher.Matches(probe, "button:not(:is(.ghost, .primary))"));
        }

        [Fact]
        public void DescendantSelector_WithTagAndIdAncestor_DoesNotFastRejectValidMatch()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <nav id='site'>
        <div id='top-logo-and-name'></div>
    </nav>
</body></html>");

            var target = ById(doc, "top-logo-and-name");

            Assert.True(SelectorMatcher.Matches(target, "nav#site #top-logo-and-name"));
            Assert.True(SelectorMatcher.Matches(target, "#site #top-logo-and-name"));
        }

        [Fact]
        public async Task EscapedSpaceIdSelector_MatchesAndAppliesCascade()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        #\  { display: none; }
    </style>
</head>
<body>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var hidden = doc.CreateElement("div");
            hidden.Id = " ";
            hidden.AppendChild(doc.CreateTextNode("FAIL"));
            doc.Body.AppendChild(hidden);

            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            Assert.True(SelectorMatcher.Matches(hidden, @"#\ "));
            Assert.Equal("none", computed[hidden].Display);
        }

        [Fact]
        public void EscapedSpaceIdDescendantSelector_DoesNotMatchWithoutEscapedAncestor()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <p id='result'><span id='score'>66</span></p>
    <div id=' '>FAIL</div>
</body></html>");

            var result = ById(doc, "result");
            var score = ById(doc, "score");

            Assert.False(SelectorMatcher.Matches(result, @"#\  #result"));
            Assert.False(SelectorMatcher.Matches(score, @"#\  #score"));
        }

        [Fact]
        public async Task CascadeAppliesNthChildOfSelectorRules()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        li { color: black; }
        li.keep { color: blue; }
        li:nth-child(2 of .keep) { color: red; }
    </style>
</head>
<body>
    <ul>
        <li id='x1' class='keep'>x1</li>
        <li id='x2'>x2</li>
        <li id='x3' class='keep'>x3</li>
        <li id='x4' class='keep'>x4</li>
    </ul>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var x3 = ById(doc, "x3");
            var x4 = ById(doc, "x4");

            Assert.Equal("red", computed[x3].Map["color"]);
            Assert.Equal("blue", computed[x4].Map["color"]);
        }

        [Fact]
        public void CompoundSelector_DoesNotMatchWhenRequiredClassIsMissing()
        {
            var doc = Parse(@"
<!doctype html>
<html class='wp25eastereggs-enable-clientpref-1'><body>
    <div id='notice' class='wp25eastereggs-sitenotice'></div>
</body></html>");

            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");

            Assert.False(
                SelectorMatcher.Matches(
                    root,
                    "html.wp25eastereggs-companion-enabled.wp25eastereggs-enable-clientpref-1:not(.ve-active)"));
        }

        [Fact]
        public void EnabledPseudoClass_DoesNotMatchNonFormElements()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='plain'></div>
    <input id='control' />
</body></html>");

            var plain = ById(doc, "plain");
            var control = ById(doc, "control");

            Assert.False(SelectorMatcher.Matches(plain, ":enabled"));
            Assert.True(SelectorMatcher.Matches(control, ":enabled"));
        }

        [Fact]
        public void TargetPseudoClass_MatchesElementWithCurrentFragment()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <section id='hero'></section>
    <section id='other'></section>
</body></html>");

            var hero = ById(doc, "hero");
            var other = ById(doc, "other");

            ElementStateManager.Instance.SetTargetFragment("hero");

            Assert.True(SelectorMatcher.Matches(hero, ":target"));
            Assert.False(SelectorMatcher.Matches(other, ":target"));
        }

        [Fact]
        public void TargetWithinPseudoClass_MatchesAncestorOfTarget()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <main id='root'>
        <section id='hero'></section>
    </main>
    <main id='other-root'>
        <section id='other'></section>
    </main>
</body></html>");

            var root = ById(doc, "root");
            var otherRoot = ById(doc, "other-root");

            ElementStateManager.Instance.SetTargetFragment("hero");

            Assert.True(SelectorMatcher.Matches(root, ":target-within"));
            Assert.False(SelectorMatcher.Matches(otherRoot, ":target-within"));
        }

        [Fact]
        public void RequiredPseudoClass_MatchesRequiredFormControlsOnly()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='required' required />
    <input id='plain' />
    <div id='non-form' required></div>
</body></html>");

            var required = ById(doc, "required");
            var plain = ById(doc, "plain");
            var nonForm = ById(doc, "non-form");

            Assert.True(SelectorMatcher.Matches(required, ":required"));
            Assert.False(SelectorMatcher.Matches(plain, ":required"));
            Assert.False(SelectorMatcher.Matches(nonForm, ":required"));
        }

        [Fact]
        public void OptionalPseudoClass_MatchesNonRequiredFormControlsOnly()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='required' required />
    <input id='plain' />
    <div id='non-form'></div>
</body></html>");

            var required = ById(doc, "required");
            var plain = ById(doc, "plain");
            var nonForm = ById(doc, "non-form");

            Assert.False(SelectorMatcher.Matches(required, ":optional"));
            Assert.True(SelectorMatcher.Matches(plain, ":optional"));
            Assert.False(SelectorMatcher.Matches(nonForm, ":optional"));
        }

        [Fact]
        public void ValidPseudoClass_MatchesFormControlsPassingValidation()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='email-valid' type='email' required value='ok@example.com' />
    <input id='email-invalid' type='email' required value='invalid' />
</body></html>");

            var emailValid = ById(doc, "email-valid");
            var emailInvalid = ById(doc, "email-invalid");

            Assert.True(SelectorMatcher.Matches(emailValid, ":valid"));
            Assert.False(SelectorMatcher.Matches(emailInvalid, ":valid"));
        }

        [Fact]
        public void InvalidPseudoClass_MatchesFormControlsFailingValidation()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='email-valid' type='email' required value='ok@example.com' />
    <input id='email-invalid' type='email' required value='invalid' />
</body></html>");

            var emailValid = ById(doc, "email-valid");
            var emailInvalid = ById(doc, "email-invalid");

            Assert.False(SelectorMatcher.Matches(emailValid, ":invalid"));
            Assert.True(SelectorMatcher.Matches(emailInvalid, ":invalid"));
        }

        [Fact]
        public void InRangePseudoClass_MatchesRangedInputsWithinBounds()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='in' type='number' min='10' max='20' value='15' />
    <input id='out' type='number' min='10' max='20' value='25' />
</body></html>");

            var inRange = ById(doc, "in");
            var outOfRange = ById(doc, "out");

            Assert.True(SelectorMatcher.Matches(inRange, ":in-range"));
            Assert.False(SelectorMatcher.Matches(outOfRange, ":in-range"));
        }

        [Fact]
        public void OutOfRangePseudoClass_MatchesRangedInputsOutsideBounds()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='in' type='number' min='10' max='20' value='15' />
    <input id='out' type='number' min='10' max='20' value='25' />
</body></html>");

            var inRange = ById(doc, "in");
            var outOfRange = ById(doc, "out");

            Assert.False(SelectorMatcher.Matches(inRange, ":out-of-range"));
            Assert.True(SelectorMatcher.Matches(outOfRange, ":out-of-range"));
        }

        [Fact]
        public void ReadOnlyPseudoClass_MatchesNonEditableElements()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='readonly-input' readonly value='x' />
    <input id='editable-input' value='x' />
    <div id='plain-div'></div>
    <div id='editable-div' contenteditable='true'></div>
</body></html>");

            var readonlyInput = ById(doc, "readonly-input");
            var editableInput = ById(doc, "editable-input");
            var plainDiv = ById(doc, "plain-div");
            var editableDiv = ById(doc, "editable-div");

            Assert.True(SelectorMatcher.Matches(readonlyInput, ":read-only"));
            Assert.False(SelectorMatcher.Matches(editableInput, ":read-only"));
            Assert.True(SelectorMatcher.Matches(plainDiv, ":read-only"));
            Assert.False(SelectorMatcher.Matches(editableDiv, ":read-only"));
        }

        [Fact]
        public void ReadWritePseudoClass_MatchesEditableControlsAndContentEditable()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='readonly-input' readonly value='x' />
    <input id='editable-input' value='x' />
    <div id='plain-div'></div>
    <div id='editable-div' contenteditable='true'></div>
</body></html>");

            var readonlyInput = ById(doc, "readonly-input");
            var editableInput = ById(doc, "editable-input");
            var plainDiv = ById(doc, "plain-div");
            var editableDiv = ById(doc, "editable-div");

            Assert.False(SelectorMatcher.Matches(readonlyInput, ":read-write"));
            Assert.True(SelectorMatcher.Matches(editableInput, ":read-write"));
            Assert.False(SelectorMatcher.Matches(plainDiv, ":read-write"));
            Assert.True(SelectorMatcher.Matches(editableDiv, ":read-write"));
        }

        [Fact]
        public void PlaceholderShownPseudoClass_MatchesEmptyControlsWithPlaceholder()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='shown' placeholder='Type here' />
    <input id='hidden' placeholder='Type here' value='x' />
</body></html>");

            var shown = ById(doc, "shown");
            var hidden = ById(doc, "hidden");

            Assert.True(SelectorMatcher.Matches(shown, ":placeholder-shown"));
            Assert.False(SelectorMatcher.Matches(hidden, ":placeholder-shown"));
        }

        [Fact]
        public void LangPseudoClass_MatchesInheritedLanguageRanges()
        {
            var doc = Parse(@"
<!doctype html>
<html lang='en-US'><body>
    <p id='target'>Hello</p>
</body></html>");

            var target = ById(doc, "target");

            Assert.True(SelectorMatcher.Matches(target, ":lang(en)"));
            Assert.True(SelectorMatcher.Matches(target, ":lang(en-us)"));
            Assert.False(SelectorMatcher.Matches(target, ":lang(fr)"));
        }

        [Fact]
        public void DefaultPseudoClass_MatchesDefaultCheckedAndSelectedControls()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='checked-radio' type='radio' checked />
    <input id='plain-radio' type='radio' />
    <select>
        <option id='selected-option' selected>One</option>
        <option id='plain-option'>Two</option>
    </select>
</body></html>");

            var checkedRadio = ById(doc, "checked-radio");
            var plainRadio = ById(doc, "plain-radio");
            var selectedOption = ById(doc, "selected-option");
            var plainOption = ById(doc, "plain-option");

            Assert.True(SelectorMatcher.Matches(checkedRadio, ":default"));
            Assert.False(SelectorMatcher.Matches(plainRadio, ":default"));
            Assert.True(SelectorMatcher.Matches(selectedOption, ":default"));
            Assert.False(SelectorMatcher.Matches(plainOption, ":default"));
        }

        [Fact]
        public void IndeterminatePseudoClass_MatchesCheckboxMixedOrProgressWithoutValue()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='mixed' type='checkbox' aria-checked='mixed' />
    <input id='checked' type='checkbox' checked />
    <progress id='progress-indeterminate'></progress>
    <progress id='progress-determinate' value='5'></progress>
</body></html>");

            var mixed = ById(doc, "mixed");
            var checkedBox = ById(doc, "checked");
            var indeterminateProgress = ById(doc, "progress-indeterminate");
            var determinateProgress = ById(doc, "progress-determinate");

            Assert.True(SelectorMatcher.Matches(mixed, ":indeterminate"));
            Assert.False(SelectorMatcher.Matches(checkedBox, ":indeterminate"));
            Assert.True(SelectorMatcher.Matches(indeterminateProgress, ":indeterminate"));
            Assert.False(SelectorMatcher.Matches(determinateProgress, ":indeterminate"));
        }

        [Fact]
        public void OpenPseudoClass_MatchesOpenDetailsAndDialog()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <details id='details-open' open></details>
    <details id='details-closed'></details>
    <dialog id='dialog-open' open></dialog>
</body></html>");

            var detailsOpen = ById(doc, "details-open");
            var detailsClosed = ById(doc, "details-closed");
            var dialogOpen = ById(doc, "dialog-open");

            Assert.True(SelectorMatcher.Matches(detailsOpen, ":open"));
            Assert.False(SelectorMatcher.Matches(detailsClosed, ":open"));
            Assert.True(SelectorMatcher.Matches(dialogOpen, ":open"));
        }

        [Fact]
        public void ClosedPseudoClass_MatchesClosedDetailsAndDialog()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <details id='details-open' open></details>
    <details id='details-closed'></details>
    <dialog id='dialog-closed'></dialog>
</body></html>");

            var detailsOpen = ById(doc, "details-open");
            var detailsClosed = ById(doc, "details-closed");
            var dialogClosed = ById(doc, "dialog-closed");

            Assert.False(SelectorMatcher.Matches(detailsOpen, ":closed"));
            Assert.True(SelectorMatcher.Matches(detailsClosed, ":closed"));
            Assert.True(SelectorMatcher.Matches(dialogClosed, ":closed"));
        }

        [Fact]
        public void ModalPseudoClass_MatchesOpenDialogInModalTopLayer()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <dialog id='modal' open data-top-layer='modal'></dialog>
    <dialog id='non-modal' open></dialog>
</body></html>");

            var modal = ById(doc, "modal");
            var nonModal = ById(doc, "non-modal");

            Assert.True(SelectorMatcher.Matches(modal, ":modal"));
            Assert.False(SelectorMatcher.Matches(nonModal, ":modal"));
        }

        [Fact]
        public void DefinedPseudoClass_DistinguishesUpgradedCustomElements()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <x-ready id='defined-ce' data-ce-upgraded='true'></x-ready>
    <x-pending id='undefined-ce'></x-pending>
    <div id='builtin'></div>
</body></html>");

            var definedCe = ById(doc, "defined-ce");
            var undefinedCe = ById(doc, "undefined-ce");
            var builtin = ById(doc, "builtin");

            Assert.True(SelectorMatcher.Matches(definedCe, ":defined"));
            Assert.False(SelectorMatcher.Matches(undefinedCe, ":defined"));
            Assert.True(SelectorMatcher.Matches(builtin, ":defined"));
        }

        [Fact]
        public void LocalLinkPseudoClass_MatchesSameOriginHyperlinks()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <a id='local' href='/docs'>Local</a>
    <a id='remote' href='https://other.example.org/docs'>Remote</a>
</body></html>");

            doc.URL = "https://example.org/home";
            doc.BaseURI = "https://example.org/home";

            var local = ById(doc, "local");
            var remote = ById(doc, "remote");

            Assert.True(SelectorMatcher.Matches(local, ":local-link"));
            Assert.False(SelectorMatcher.Matches(remote, ":local-link"));
        }

        [Fact]
        public void BlankPseudoClass_MatchesWhitespaceOnlyInputs()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='blank' value='   ' />
    <input id='filled' value='x' />
</body></html>");

            var blank = ById(doc, "blank");
            var filled = ById(doc, "filled");

            Assert.True(SelectorMatcher.Matches(blank, ":blank"));
            Assert.False(SelectorMatcher.Matches(filled, ":blank"));
        }

        [Fact]
        public void DirPseudoClass_DerivesDirectionFromDirAutoContent()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='rtl' dir='auto'>שלום</div>
    <div id='ltr' dir='auto'>Hello</div>
</body></html>");

            var rtl = ById(doc, "rtl");
            var ltr = ById(doc, "ltr");

            Assert.True(SelectorMatcher.Matches(rtl, ":dir(rtl)"));
            Assert.False(SelectorMatcher.Matches(rtl, ":dir(ltr)"));
            Assert.True(SelectorMatcher.Matches(ltr, ":dir(ltr)"));
            Assert.False(SelectorMatcher.Matches(ltr, ":dir(rtl)"));
        }

        [Fact]
        public void LinkPseudoClass_MatchesLinkElementsWithHref()
        {
            var doc = Parse(@"
<!doctype html>
<html><head>
    <link id='stylesheet-link' rel='stylesheet' href='/site.css'>
</head><body>
    <a id='anchor-link' href='/docs'>Docs</a>
</body></html>");

            var stylesheetLink = ById(doc, "stylesheet-link");
            var anchorLink = ById(doc, "anchor-link");

            Assert.True(SelectorMatcher.Matches(stylesheetLink, ":link"));
            Assert.True(SelectorMatcher.Matches(anchorLink, ":link"));
        }

        [Fact]
        public void AnyLinkPseudoClass_MatchesAnchorAreaAndLinkElements()
        {
            var doc = Parse(@"
<!doctype html>
<html><head>
    <link id='stylesheet-link' rel='stylesheet' href='/site.css'>
</head><body>
    <a id='anchor-link' href='/docs'>Docs</a>
    <area id='area-link' href='/map' />
</body></html>");

            var stylesheetLink = ById(doc, "stylesheet-link");
            var anchorLink = ById(doc, "anchor-link");
            var areaLink = ById(doc, "area-link");

            Assert.True(SelectorMatcher.Matches(stylesheetLink, ":any-link"));
            Assert.True(SelectorMatcher.Matches(anchorLink, ":any-link"));
            Assert.True(SelectorMatcher.Matches(areaLink, ":any-link"));
        }

        [Fact]
        public void DisabledPseudoClass_RespectsFieldsetAndOptGroupInheritance()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <fieldset disabled>
        <legend><input id='legend-input' /></legend>
        <input id='fieldset-input' />
    </fieldset>
    <select>
        <optgroup disabled>
            <option id='disabled-option'>A</option>
        </optgroup>
    </select>
</body></html>");

            var legendInput = ById(doc, "legend-input");
            var fieldsetInput = ById(doc, "fieldset-input");
            var disabledOption = ById(doc, "disabled-option");

            Assert.False(SelectorMatcher.Matches(legendInput, ":disabled"));
            Assert.True(SelectorMatcher.Matches(fieldsetInput, ":disabled"));
            Assert.True(SelectorMatcher.Matches(disabledOption, ":disabled"));
        }

        [Fact]
        public void EnabledPseudoClass_ExcludesEffectivelyDisabledControls()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <fieldset disabled>
        <legend><input id='legend-input' /></legend>
        <input id='fieldset-input' />
    </fieldset>
    <select>
        <optgroup disabled>
            <option id='disabled-option'>A</option>
        </optgroup>
        <option id='enabled-option'>B</option>
    </select>
</body></html>");

            var legendInput = ById(doc, "legend-input");
            var fieldsetInput = ById(doc, "fieldset-input");
            var disabledOption = ById(doc, "disabled-option");
            var enabledOption = ById(doc, "enabled-option");

            Assert.True(SelectorMatcher.Matches(legendInput, ":enabled"));
            Assert.False(SelectorMatcher.Matches(fieldsetInput, ":enabled"));
            Assert.False(SelectorMatcher.Matches(disabledOption, ":enabled"));
            Assert.True(SelectorMatcher.Matches(enabledOption, ":enabled"));
        }

        [Fact]
        public void FocusWithinPseudoClass_MatchesFocusedElementAndAncestors()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='host'><input id='child' /></div>
    <div id='other'><input id='other-child' /></div>
</body></html>");

            var host = ById(doc, "host");
            var child = ById(doc, "child");
            var other = ById(doc, "other");

            ElementStateManager.Instance.SetFocusedElement(child);

            Assert.True(SelectorMatcher.Matches(host, ":focus-within"));
            Assert.True(SelectorMatcher.Matches(child, ":focus-within"));
            Assert.False(SelectorMatcher.Matches(other, ":focus-within"));
        }

        [Fact]
        public void FocusVisiblePseudoClass_TracksKeyboardFocusHeuristic()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <button id='button'>Go</button>
</body></html>");

            var button = ById(doc, "button");

            ElementStateManager.Instance.SetFocusedElement(button, fromKeyboard: false);
            Assert.False(SelectorMatcher.Matches(button, ":focus-visible"));

            ElementStateManager.Instance.SetFocusedElement(button, fromKeyboard: true);
            Assert.True(SelectorMatcher.Matches(button, ":focus-visible"));
        }

        [Fact]
        public void UserValidPseudoClass_MatchesOnlyAfterInteractionMarker()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='valid-user' type='email' value='ok@example.com' data-user-interacted='true' />
    <input id='valid-pristine' type='email' value='ok@example.com' />
</body></html>");

            var validUser = ById(doc, "valid-user");
            var validPristine = ById(doc, "valid-pristine");

            Assert.True(SelectorMatcher.Matches(validUser, ":user-valid"));
            Assert.False(SelectorMatcher.Matches(validPristine, ":user-valid"));
        }

        [Fact]
        public void UserInvalidPseudoClass_MatchesInvalidControlsAfterInteractionMarker()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='invalid-user' type='email' value='broken' data-user-interacted='true' />
    <input id='invalid-pristine' type='email' value='broken' />
</body></html>");

            var invalidUser = ById(doc, "invalid-user");
            var invalidPristine = ById(doc, "invalid-pristine");

            Assert.True(SelectorMatcher.Matches(invalidUser, ":user-invalid"));
            Assert.False(SelectorMatcher.Matches(invalidPristine, ":user-invalid"));
        }

        [Fact]
        public void AutofillPseudoClass_MatchesControlsMarkedAutofilled()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <input id='filled' data-autofill='true' />
    <input id='plain' />
</body></html>");

            var filled = ById(doc, "filled");
            var plain = ById(doc, "plain");

            Assert.True(SelectorMatcher.Matches(filled, ":autofill"));
            Assert.False(SelectorMatcher.Matches(plain, ":autofill"));
        }

        [Fact]
        public void FirstPseudoClass_AliasesFirstChild()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <ul>
        <li id='first'></li>
        <li id='second'></li>
    </ul>
</body></html>");

            var first = ById(doc, "first");
            var second = ById(doc, "second");

            Assert.True(SelectorMatcher.Matches(first, ":first"));
            Assert.False(SelectorMatcher.Matches(second, ":first"));
        }

        [Fact]
        public void LastPseudoClass_AliasesLastChild()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <ul>
        <li id='first'></li>
        <li id='second'></li>
    </ul>
</body></html>");

            var first = ById(doc, "first");
            var second = ById(doc, "second");

            Assert.False(SelectorMatcher.Matches(first, ":last"));
            Assert.True(SelectorMatcher.Matches(second, ":last"));
        }

        [Fact]
        public void OnlyPseudoClass_AliasesOnlyChild()
        {
            var singleDoc = Parse(@"
<!doctype html>
<html><body>
    <div id='single'></div>
</body></html>");
            var single = ById(singleDoc, "single");
            Assert.True(SelectorMatcher.Matches(single, ":only"));

            var multiDoc = Parse(@"
<!doctype html>
<html><body>
    <div id='first'></div>
    <div id='second'></div>
</body></html>");
            var first = ById(multiDoc, "first");
            Assert.False(SelectorMatcher.Matches(first, ":only"));
        }

        [Fact]
        public void PlayingPseudoClass_MatchesMediaInPlayingState()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <video id='playing' data-media-paused='false'></video>
    <video id='paused' data-media-paused='true'></video>
</body></html>");

            var playing = ById(doc, "playing");
            var paused = ById(doc, "paused");

            Assert.True(SelectorMatcher.Matches(playing, ":playing"));
            Assert.False(SelectorMatcher.Matches(paused, ":playing"));
        }

        [Fact]
        public void PausedPseudoClass_MatchesMediaInPausedState()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <video id='playing' data-media-paused='false'></video>
    <video id='paused' data-media-paused='true'></video>
</body></html>");

            var playing = ById(doc, "playing");
            var paused = ById(doc, "paused");

            Assert.False(SelectorMatcher.Matches(playing, ":paused"));
            Assert.True(SelectorMatcher.Matches(paused, ":paused"));
        }

        [Fact]
        public void SeekingPseudoClass_MatchesMediaSeekingState()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <video id='seeking' data-media-seeking='true'></video>
    <video id='steady'></video>
</body></html>");

            var seeking = ById(doc, "seeking");
            var steady = ById(doc, "steady");

            Assert.True(SelectorMatcher.Matches(seeking, ":seeking"));
            Assert.False(SelectorMatcher.Matches(steady, ":seeking"));
        }

        [Fact]
        public void StalledPseudoClass_MatchesMediaStalledState()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <video id='stalled' data-media-stalled='true'></video>
    <video id='steady'></video>
</body></html>");

            var stalled = ById(doc, "stalled");
            var steady = ById(doc, "steady");

            Assert.True(SelectorMatcher.Matches(stalled, ":stalled"));
            Assert.False(SelectorMatcher.Matches(steady, ":stalled"));
        }

        [Fact]
        public void MutedPseudoClass_MatchesMutedMediaElements()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <video id='muted' muted></video>
    <video id='audible'></video>
</body></html>");

            var muted = ById(doc, "muted");
            var audible = ById(doc, "audible");

            Assert.True(SelectorMatcher.Matches(muted, ":muted"));
            Assert.False(SelectorMatcher.Matches(audible, ":muted"));
        }

        [Fact]
        public void VolumeLockedPseudoClass_MatchesMediaWithVolumeLockMarker()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <video id='locked' data-media-volume-locked='true'></video>
    <video id='normal'></video>
</body></html>");

            var locked = ById(doc, "locked");
            var normal = ById(doc, "normal");

            Assert.True(SelectorMatcher.Matches(locked, ":volume-locked"));
            Assert.False(SelectorMatcher.Matches(normal, ":volume-locked"));
        }

        [Fact]
        public void MalformedCompoundSelector_IsRejected()
        {
            var doc = Parse(@"
<!doctype html>
<html class='test'><body></body></html>");

            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");

            Assert.False(SelectorMatcher.Matches(root, "html*.test"));
        }

        [Fact]
        public async Task Cascade_KeepsWikipediaEasterEggNoticeHiddenWithoutCompanionClass()
        {
            const string html = @"
<!doctype html>
<html class='wp25eastereggs-enable-clientpref-1'>
<head>
    <style>
        .wp25eastereggs-sitenotice,
        .wp25eastereggs-sitenotice-learn-more-link,
        .wp25eastereggs-vector-sitenotice-landmark,
        .wp25eastereggs-video-container { display: none; }

        @media screen and (min-width: 1120px) {
            html.wp25eastereggs-companion-enabled.wp25eastereggs-enable-clientpref-1:not(.ve-active) .wp25eastereggs-vector-sitenotice-landmark {
                display: block;
                width: 12.25rem;
                height: 12.25rem;
            }
        }
    </style>
</head>
<body>
    <div class='vector-sitenotice-container'>
        <div id='siteNotice'>
            <div class='wp25eastereggs-sitenotice'>
                <div id='landmark' class='wp25eastereggs-vector-sitenotice-landmark'></div>
            </div>
        </div>
    </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://en.wikipedia.org"), null);

            var notice = ById(doc, "siteNotice").Descendants().OfType<Element>()
                .First(e => e.ClassName?.Contains("wp25eastereggs-sitenotice", StringComparison.Ordinal) == true);
            var landmark = ById(doc, "landmark");

            Assert.Equal("none", computed[notice].Display);
            Assert.Equal("none", computed[landmark].Display);
        }

        [Fact]
        public async Task Cascade_AppliesWikipediaToolbarFloatSelectorList()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        .vector-menu-tabs .mw-list-item,.vector-page-toolbar-container .vector-dropdown { float: left; margin-bottom: 0; }
    </style>
</head>
<body>
    <div class='vector-page-toolbar-container'>
        <div class='vector-menu-tabs'>
            <ul class='vector-menu-content-list'>
                <li id='ca-view' class='selected vector-tab-noicon mw-list-item'>Read</li>
                <li id='ca-viewsource' class='vector-tab-noicon mw-list-item'>View source</li>
            </ul>
        </div>
    </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://en.wikipedia.org"), null);

            var readTab = ById(doc, "ca-view");
            var sourceTab = ById(doc, "ca-viewsource");

            Assert.Equal("left", computed[readTab].Float);
            Assert.Equal("left", computed[sourceTab].Float);
        }

        [Fact]
        public async Task Cascade_AppliesFirstChildPseudoClassRule_ToLeadingHeading()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        html { font-size: 20px; }
        h1 { font-size: 2em; }
        h1:first-child { font-size: 5em; }
        #result { font-size: 5em; }
    </style>
</head>
<body>
    <h1 id='title'>Acid3</h1>
    <div id='result'>64</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid3.acidtests.org/"), null);

            var title = ById(doc, "title");
            var result = ById(doc, "result");

            Assert.Equal("5em", computed[title].Map["font-size"]);
            Assert.InRange(computed[title].FontSize ?? 0d, 99.999d, 100.001d);
            Assert.Equal("5em", computed[result].Map["font-size"]);
            Assert.InRange(computed[result].FontSize ?? 0d, 99.999d, 100.001d);
        }

        [Fact]
        public async Task Cascade_Acid3HeaderRules_SurviveDataUrlBodyDeclaration()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style type='text/css'>
        * { margin: 0; border: 1px blue; padding: 0; border-spacing: 0; font: inherit; line-height: 1.2; color: inherit; background: transparent; }
        html { font: 20px Arial, sans-serif; border: 2cm solid gray; width: 32em; margin: 1em; }
        :root { background: silver; color: black; border-width: 0 0.2em 0.2em 0; }
        body { padding: 2em 2em 0; background: url(data:image/gif;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAUCAIAAAAC64paAAAABGdBTUEAAK/INwWK6QAAAAlwSFlzAAAASAAAAEgARslrPgAAABtJREFUOMtj/M9APmCiQO+o5lHNo5pHNVNBMwAinAEnIWw89gAAACJ6VFh0U29mdHdhcmUAAHjac0zJT0pV8MxNTE8NSk1MqQQAL5wF1K4MqU0AAAAASUVORK5CYII=) no-repeat 99.8392283% 1px white; border: solid 1px black; margin: -0.2em 0 0 -0.2em; }
        h1:first-child { cursor: help; font-size: 5em; font-weight: bolder; margin-bottom: -0.4em; text-shadow: rgba(192, 192, 192, 1.0) 3px 3px; }
        #result { font-weight: bolder; width: 5.68em; text-align: right; }
        #result { font-size: 5em; margin: -2.19em 0 0; }
    </style>
</head>
<body>
    <h1 id='title'>Acid3</h1>
    <div id='result'>64</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid3.acidtests.org/"), null);

            var title = ById(doc, "title");
            var result = ById(doc, "result");

            Assert.Equal("5em", computed[title].Map["font-size"]);
            Assert.Equal("5em", computed[result].Map["font-size"]);
        }

        [Fact]
        public void ParseRules_Acid3HeaderRules_AreNotDroppedAfterDataUrlBodyDeclaration()
        {
            const string css = @"
                * { margin: 0; border: 1px blue; padding: 0; border-spacing: 0; font: inherit; line-height: 1.2; color: inherit; background: transparent; }
                html { font: 20px Arial, sans-serif; border: 2cm solid gray; width: 32em; margin: 1em; }
                :root { background: silver; color: black; border-width: 0 0.2em 0.2em 0; }
                body { padding: 2em 2em 0; background: url(data:image/gif;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAUCAIAAAAC64paAAAABGdBTUEAAK/INwWK6QAAAAlwSFlzAAAASAAAAEgARslrPgAAABtJREFUOMtj/M9APmCiQO+o5lHNo5pHNVNBMwAinAEnIWw89gAAACJ6VFh0U29mdHdhcmUAAHjac0zJT0pV8MxNTE8NSk1MqQQAL5wF1K4MqU0AAAAASUVORK5CYII=) no-repeat 99.8392283% 1px white; border: solid 1px black; margin: -0.2em 0 0 -0.2em; }
                h1:first-child { cursor: help; font-size: 5em; font-weight: bolder; margin-bottom: -0.4em; text-shadow: rgba(192, 192, 192, 1.0) 3px 3px; }
                #result { font-weight: bolder; width: 5.68em; text-align: right; }
                #result { font-size: 5em; margin: -2.19em 0 0; }";

            var parseRules = typeof(CssLoader).GetMethod("ParseRules", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(parseRules);

            var rules = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
                parseRules!.Invoke(null, new object[] { css, 0, new Uri("https://acid3.acidtests.org/"), null, null, null, CssOrigin.Author }));

            var styleSelectors = rules.Cast<CssRule>()
                .OfType<CssStyleRule>()
                .Select(rule => rule.Selector?.Raw)
                .Where(raw => !string.IsNullOrWhiteSpace(raw))
                .ToArray();

            Assert.Contains("h1:first-child", styleSelectors);
            Assert.Equal(2, styleSelectors.Count(raw => raw == "#result"));
        }

        private static Document Parse(string html)
        {
            var parser = new HtmlParser(html);
            return parser.Parse();
        }

        private static Element ById(Document doc, string id)
        {
            return doc.Descendants().OfType<Element>().First(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        }
    }
}
