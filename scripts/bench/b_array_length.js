// The single most common idiom in JavaScript, and the property read behind it.
//
// `a.length` on a dense array is not a slot in any shape - the array
// synthesises it from its vector - so until a cache program could describe
// that, every loop bound in a page took the full generic property path. This
// rung is what that costs.
var a = [];
for (var i = 0; i < 4096; i++) a.push(i);
var o = { length: 4096 };

function loopOnly(n)   { var t = 0; for (var i = 0; i < n; i++) t += 1; return t; }
function readLen(n)    { var t = 0; for (var i = 0; i < n; i++) t += a.length; return t; }
function readObjLen(n) { var t = 0; for (var i = 0; i < n; i++) t += o.length; return t; }
function sumArray(n)   { var t = 0; for (var k = 0; k < n; k += 4096) { for (var i = 0; i < a.length; i++) t += a[i]; } return t; }

var rungs = [["loop", loopOnly], ["a.length", readLen], ["o.length", readObjLen], ["sum via a.length", sumArray]];
for (var r = 0; r < rungs.length; r++) rungs[r][1](200000);
var N = 4000000, out = "", prev = 0;
for (var r = 0; r < rungs.length; r++) {
    var best = 1e18;
    for (var k = 0; k < 3; k++) {
        var t0 = Date.now(); rungs[r][1](N); var ms = Date.now() - t0;
        if (ms < best) best = ms;
    }
    var ns = best * 1e6 / N;
    out += rungs[r][0] + "=" + ns.toFixed(0) + "ns ";
}
out;
