// Does a property read cost more when the site has seen many shapes?
//
// A page's bundle reads properties off objects of dozens of different shapes
// through the same few lines of code; a microbenchmark reads one shape and
// reports a number the page never sees. Each rung reads the same count of
// properties; only the number of distinct shapes at the site changes.
function makeShapes(kinds, count) {
    var out = [];
    for (var i = 0; i < count; i++) {
        var o = {};
        // Distinct key order per kind gives each its own shape.
        var k = i % kinds;
        for (var j = 0; j <= k; j++) o["pad" + j] = j;
        o.hit = i;
        out.push(o);
    }
    return out;
}

function readAll(objs, n) {
    var t = 0;
    for (var i = 0; i < n; i++) t += objs[i & 4095].hit;
    return t;
}

var rungs = [1, 2, 4, 8, 16, 64];
var sets = [];
for (var r = 0; r < rungs.length; r++) sets.push(makeShapes(rungs[r], 4096));
for (var r = 0; r < sets.length; r++) readAll(sets[r], 100000);

var N = 3000000, out = "";
for (var r = 0; r < sets.length; r++) {
    var best = 1e18;
    for (var k = 0; k < 3; k++) {
        var t0 = Date.now(); readAll(sets[r], N); var ms = Date.now() - t0;
        if (ms < best) best = ms;
    }
    out += rungs[r] + "shapes=" + (best * 1e6 / N).toFixed(0) + "ns ";
}
out;
