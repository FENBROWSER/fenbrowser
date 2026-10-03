// Many distinct hidden classes: every object starts a new transition branch,
// the way JSON-ish data and per-instance expandos do on real pages.
var start = Date.now();
var keep = [];
for (var round = 0; round < 5; round++) {
    for (var i = 0; i < 40000; i++) {
        var o = {};
        o['k' + i] = i;
        o.a = 1;
        o.b = 2;
        o['m' + (i % 97)] = 3;
        keep.push(o);
    }
    keep.length = 0;
}
var elapsed = Date.now() - start;
typeof console !== 'undefined' ? console.log('shape churn ms: ' + elapsed) : elapsed;
