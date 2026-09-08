// Is a property read slow because of the work, or because of where the data is?
//
// Same code, same shape, same instruction count at every rung: only the number
// of live objects walked changes, from a set that fits in cache to one the size
// a real page carries. If the cost tracks the working set, the engine is not
// spending its time computing - it is waiting for memory, and the lever is the
// size and the number of hops of the data rather than the code.
function build(count) {
    var out = [];
    for (var i = 0; i < count; i++) out.push({ hit: i, b: 0, c: 0 });
    return out;
}
function readAll(objs, mask, n) {
    var t = 0;
    for (var i = 0; i < n; i++) t += objs[i & mask].hit;
    return t;
}
var sizes = [64, 1024, 16384, 262144, 1048576];
var sets = [];
for (var s = 0; s < sizes.length; s++) sets.push(build(sizes[s]));
for (var s = 0; s < sets.length; s++) readAll(sets[s], sizes[s] - 1, 100000);
var N = 2000000, out = "";
for (var s = 0; s < sets.length; s++) {
    var best = 1e18;
    for (var k = 0; k < 3; k++) {
        var t0 = Date.now(); readAll(sets[s], sizes[s] - 1, N); var ms = Date.now() - t0;
        if (ms < best) best = ms;
    }
    out += sizes[s] + "obj=" + (best * 1e6 / N).toFixed(0) + "ns ";
}
out;
