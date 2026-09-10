// What a property costs when the receiver is a string primitive.
//
// A string has no shape, so before the string cache forms every read off one
// missed its site: `length` fell through the whole [[Get]] switch, and a method
// name resolved the realm's String.prototype through two dictionary lookups on
// the global before walking it. On reCAPTCHA's bundle those two names were 80%
// of what still missed a cache site.
//
// `o.length own` is the control - an ordinary own read, which must not move.
var s = "abcdefghij";
var o = { length: 10, charCodeAt: 0 };

function loopOnly(n)   { var t = 0; for (var i = 0; i < n; i++) t += 1; return t; }
function ownLength(n)  { var t = 0; for (var i = 0; i < n; i++) t += o.length; return t; }
function strLength(n)  { var t = 0; for (var i = 0; i < n; i++) t += s.length; return t; }
function strMethod(n)  { var t = 0; for (var i = 0; i < n; i++) t += (s.charCodeAt ? 1 : 0); return t; }
function strCall(n)    { var t = 0; for (var i = 0; i < n; i++) t += s.charCodeAt(0); return t; }
function strIndex(n)   { var t = 0; for (var i = 0; i < n; i++) t += (s[0] === "a" ? 1 : 0); return t; }

var rungs = [["loop", loopOnly], ["o.length own", ownLength], ["s.length", strLength],
             ["s.charCodeAt read", strMethod], ["s.charCodeAt(0)", strCall], ["s[0]", strIndex]];
for (var r = 0; r < rungs.length; r++) rungs[r][1](200000);
var N = 3000000, out = "";
for (var r = 0; r < rungs.length; r++) {
    var best = 1e18;
    for (var k = 0; k < 3; k++) { var t0 = Date.now(); rungs[r][1](N); var ms = Date.now() - t0; if (ms < best) best = ms; }
    out += rungs[r][0] + "=" + (best * 1e6 / N).toFixed(0) + "ns ";
}
out;
