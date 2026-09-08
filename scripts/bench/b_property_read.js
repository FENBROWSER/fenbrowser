// The cost of one property read, with nothing else in the way.
//
// Everything here is in L1 - one object, one shape, one site - so what is left
// is what the engine does per read rather than what it waits for.
var o = { hit: 7, b: 1, c: 2 };
var arr = [1, 2, 3, 4];
var objs = [o, o, o, o];

function loopOnly(n)  { var t = 0; for (var i = 0; i < n; i++) t += 1; return t; }
function readLocal(n) { var t = 0; var p = o; for (var i = 0; i < n; i++) t += p.hit; return t; }
function readFree(n)  { var t = 0; for (var i = 0; i < n; i++) t += o.hit; return t; }
function readElem(n)  { var t = 0; for (var i = 0; i < n; i++) t += arr[i & 3]; return t; }
function readBoth(n)  { var t = 0; for (var i = 0; i < n; i++) t += objs[i & 3].hit; return t; }
function writeLocal(n){ var p = o; for (var i = 0; i < n; i++) p.b = i; return p.b; }

var rungs = [["loop", loopOnly], ["o.hit via local", readLocal], ["o.hit via free var", readFree],
             ["arr[i]", readElem], ["objs[i].hit", readBoth], ["p.b = i", writeLocal]];
for (var r = 0; r < rungs.length; r++) rungs[r][1](200000);
var N = 5000000, out = "";
for (var r = 0; r < rungs.length; r++) {
    var best = 1e18;
    for (var k = 0; k < 3; k++) {
        var t0 = Date.now(); rungs[r][1](N); var ms = Date.now() - t0;
        if (ms < best) best = ms;
    }
    out += rungs[r][0] + "=" + (best * 1e6 / N).toFixed(0) + "ns ";
}
out;
