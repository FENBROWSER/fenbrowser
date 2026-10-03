// Object.keys / for-in / spread over small objects - how pages copy options
// objects, merge props and iterate config.
var src = { alpha: 1, beta: 2, gamma: 3, delta: 4, epsilon: 5, zeta: 6 };
var start = Date.now();
var total = 0;
for (var i = 0; i < 200000; i++) {
    total += Object.keys(src).length;
    for (var k in src) total++;
    var copy = Object.assign({}, src);
    total += copy.alpha;
}
Date.now() - start;
