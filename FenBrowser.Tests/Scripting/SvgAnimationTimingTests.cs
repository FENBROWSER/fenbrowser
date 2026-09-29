using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// SMIL timing from script: the outermost svg element's timeline controls and
    /// the animation elements' beginElement/endElement instance times, which
    /// painting adds to the begin and end lists it renders.
    /// </summary>
    public sealed class SvgAnimationTimingTests
    {
        private const string Markup =
            "<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<rect id='r'><animate id='a' attributeName='x' to='5' dur='1s' begin='indefinite'/></rect>" +
            "<svg id='inner'><set id='s' href='#r' attributeName='y' to='1'/></svg>" +
            "</svg>";

        [Fact]
        public async Task PauseSeekAndUnpause_DriveTheOutermostTimeline()
        {
            var (engine, document) = await CreateAsync();

            var result = engine.Evaluate(
                """
                var svg = document.documentElement;
                var inner = document.getElementById('inner');
                var out = [svg.animationsPaused()];
                inner.pauseAnimations();
                svg.setCurrentTime(2.5);
                out.push(svg.animationsPaused(), svg.getCurrentTime(), inner.getCurrentTime(),
                         document.getElementById('a').getCurrentTime());
                svg.setCurrentTime(-3);
                out.push(svg.getCurrentTime());
                svg.unpauseAnimations();
                out.push(svg.animationsPaused());
                out.join('|');
                """);

            Assert.Equal("false|true|2.5|2.5|2.5|0|false", result?.ToString());
            Assert.False(SvgAnimationTimeline.IsPaused(document.DocumentElement));
        }

        [Fact]
        public async Task BeginAndEndElement_AddInstanceTimesAtTheCurrentTime()
        {
            var (engine, document) = await CreateAsync();

            engine.Evaluate(
                """
                var svg = document.documentElement;
                svg.pauseAnimations();
                svg.setCurrentTime(2);
                var a = document.getElementById('a');
                a.beginElement();
                a.beginElementAt(0.5);
                a.endElementAt(3);
                document.getElementById('s').beginElement();
                """);

            var animate = document.GetElementById("a");
            Assert.Equal("indefinite;2s;2.5s", SvgAnimationTimeline.ComposeTimingAttribute(animate, isEnd: false));
            Assert.Equal("5s", SvgAnimationTimeline.ComposeTimingAttribute(animate, isEnd: true));
            // A missing begin defaults to 0, which stays in the list.
            Assert.Equal("0;2s", SvgAnimationTimeline.ComposeTimingAttribute(document.GetElementById("s"), isEnd: false));
        }

        [Fact]
        public async Task TargetElement_IsTheHrefTargetOrTheParent()
        {
            var (engine, _) = await CreateAsync();

            var result = engine.Evaluate(
                "document.getElementById('a').targetElement.id + '|' + document.getElementById('s').targetElement.id");

            Assert.Equal("r|r", result?.ToString());
        }

        private static async Task<(FenJsBrowserScriptEngine Engine, Document Document)> CreateAsync()
        {
            var document = XmlDomParser.Parse(Markup, "image/svg+xml");
            var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(document.DocumentElement, new Uri("https://fen.test/anim.svg"));
            return (engine, document);
        }
    }
}
