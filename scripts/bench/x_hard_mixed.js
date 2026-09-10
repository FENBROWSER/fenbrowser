// HARD: the mix an actual page runs - strings, arrays, a dictionary object,
// closures and a sort with a comparator.
//
// Nothing here is a microbenchmark of one operation. It builds a corpus,
// tokenises it, counts words into a plain object, then ranks them through a
// comparator called O(n log n) times. Deterministic, so every engine does
// exactly the same work and the check value proves it.
// Output channel: node prints with console.log, the FenJS shell prints the
// script's last expression and its own `print` is a stub. Do both.

var WORDS = ['alpha', 'beta', 'gamma', 'delta', 'epsilon', 'zeta', 'eta', 'theta',
             'iota', 'kappa', 'lambda', 'mu', 'nu', 'xi', 'omicron', 'pi'];

function buildCorpus(n) {
    var parts = [];
    var seed = 12345;
    for (var i = 0; i < n; i++) {
        seed = (seed * 1103515245 + 12345) & 0x7fffffff;
        parts.push(WORDS[seed % WORDS.length]);
    }
    return parts.join(' ');
}

function rank(corpus) {
    var tokens = corpus.split(' ');
    var counts = {};
    for (var i = 0; i < tokens.length; i++) {
        var w = tokens[i];
        counts[w] = (counts[w] || 0) + 1;
    }

    var keys = Object.keys(counts);
    keys.sort(function (a, b) {
        var d = counts[b] - counts[a];
        return d !== 0 ? d : (a < b ? -1 : a > b ? 1 : 0);
    });

    var out = '';
    for (var k = 0; k < keys.length && k < 5; k++) {
        out += keys[k].charAt(0) + counts[keys[k]];
    }
    return out;
}

var corpus = buildCorpus(200000);
var ROUNDS = 12;
rank(corpus);

var best = 1e18, check = '';
for (var r = 0; r < 3; r++) {
    var t0 = Date.now();
    for (var q = 0; q < ROUNDS; q++) {
        check = rank(corpus);
    }
    var ms = Date.now() - t0;
    if (ms < best) best = ms;
}

var report = 'hard_mixed ms=' + best + ' msPerRound=' + (best / ROUNDS).toFixed(1) + ' check=' + check;
if (typeof console !== 'undefined') console.log(report);
report;
