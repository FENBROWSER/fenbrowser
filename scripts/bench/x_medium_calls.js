// MEDIUM: calls, allocation and property access - the shape a real bundle runs.
//
// Every iteration allocates an object, calls a prototype method on it and reads
// a free variable. That is the mix the register-window loop was built for: the
// frame, the call and the free-variable read are the three things it replaced.
//
// The objects go into a ring buffer that is read back, so they genuinely have
// to exist. Without that a compiler doing escape analysis deletes the
// allocation and the benchmark stops measuring allocation at all - V8 turned
// the whole loop into 3.5ns an iteration that way.
//
// Decomposed on FenJS, most of this is the `new`: a bare loop iteration is
// ~177ns, `o.x` adds nothing, `o.len2()` ~297ns, an `{x, y}` literal ~337ns -
// and `new Point()` ~1050ns, near-identical on both loops. So read a change
// here as a change to construction unless the other rungs moved too.
// Output channel: node prints with console.log, the FenJS shell prints the
// script's last expression and its own `print` is a stub. Do both.

function Point(x, y) {
    this.x = x;
    this.y = y;
}

Point.prototype.len2 = function () {
    return this.x * this.x + this.y * this.y;
};

var scale = 3;
var RING = 64;

function work(n) {
    var ring = new Array(RING);
    for (var k = 0; k < RING; k++) {
        ring[k] = new Point(0, 0);
    }

    var total = 0;
    for (var i = 0; i < n; i++) {
        var p = new Point(i & 255, (i >> 3) & 255);
        ring[i & (RING - 1)] = p;
        var older = ring[(i + 7) & (RING - 1)];
        total = (total + p.len2() * scale + older.x) | 0;
    }
    return total;
}

var N = 2000000;
work(50000);

var best = 1e18, check = 0;
for (var r = 0; r < 3; r++) {
    var t0 = Date.now();
    check = work(N);
    var ms = Date.now() - t0;
    if (ms < best) best = ms;
}

var report = 'medium_calls ms=' + best + ' nsPerIter=' + (best * 1e6 / N).toFixed(1) + ' check=' + check;
if (typeof console !== 'undefined') console.log(report);
report;
