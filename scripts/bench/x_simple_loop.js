// SIMPLE: a tight integer loop. No calls, no allocation, no property access.
//
// This is the shape a JIT is built for and the shape the register-window loop
// has no answer to, because it has no tier-up. Portable across engines: only
// Date.now and one output function.
// Output channel: node prints with console.log, the FenJS shell prints the
// script's last expression and its own `print` is a stub. Do both.

function sum(n) {
    var t = 0;
    for (var i = 0; i < n; i++) {
        t = (t + i * 3) | 0;
    }
    return t;
}

var N = 20000000;
sum(100000);

var best = 1e18, check = 0;
for (var r = 0; r < 3; r++) {
    var t0 = Date.now();
    check = sum(N);
    var ms = Date.now() - t0;
    if (ms < best) best = ms;
}

var report = 'simple_loop ms=' + best + ' nsPerIter=' + (best * 1e6 / N).toFixed(1) + ' check=' + check;
if (typeof console !== 'undefined') console.log(report);
report;
