// What a property costs when it is not on the object you asked.
//
// A method call on a class instance, on an array, on anything with a
// prototype, reads the callee off the prototype - and a cache that guards only
// the receiver's shape cannot describe that, so every one of them walked the
// chain. `p.x own` is the control: it must not move.
function Point(x) { this.x = x; }
Point.prototype.get = function () { return this.x; };
Point.prototype.tag = 7;
var p = new Point(3);
var a = [1, 2, 3, 4];

function loopOnly(n)  { var t = 0; for (var i = 0; i < n; i++) t += 1; return t; }
function ownRead(n)   { var t = 0; for (var i = 0; i < n; i++) t += p.x; return t; }
function protoData(n) { var t = 0; for (var i = 0; i < n; i++) t += p.tag; return t; }
function protoCall(n) { var t = 0; for (var i = 0; i < n; i++) t += p.get(); return t; }
function arrProto(n)  { var t = 0; for (var i = 0; i < n; i++) t += a.indexOf(3); return t; }

var rungs = [["loop", loopOnly], ["p.x own", ownRead], ["p.tag proto", protoData],
             ["p.get() proto", protoCall], ["a.indexOf proto", arrProto]];
for (var r = 0; r < rungs.length; r++) rungs[r][1](200000);
var N = 3000000, out = "";
for (var r = 0; r < rungs.length; r++) {
    var best = 1e18;
    for (var k = 0; k < 3; k++) { var t0 = Date.now(); rungs[r][1](N); var ms = Date.now() - t0; if (ms < best) best = ms; }
    out += rungs[r][0] + "=" + (best * 1e6 / N).toFixed(0) + "ns ";
}
out;
