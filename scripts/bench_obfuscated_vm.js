// Mirrors the opcode shape the reCAPTCHA bundle runs in the browser: an
// obfuscated interpreter loop that is mostly identifier reads, constants and
// moves over array indexing and bit manipulation. Measured profile of the real
// page: LoadVar 21-26%, LoadConst 16-21%, Move 9-12%, GetElem+SetElem ~9%,
// rest bitwise/compare. This is a standalone stand-in so interpreter throughput
// can be measured without a network, a DOM or a second process in the way.
function bench(n) {
    var a = new Array(256);
    for (var i = 0; i < 256; i++) {
        a[i] = i * 7 + 1;
    }

    var h = 0;
    for (var i = 0; i < n; i++) {
        var k = i & 255;
        var x = a[k];
        var y = ((x << 3) | (x >>> 5)) & 65535;
        h = (h ^ y) + (h & 1023);
        h = h & 2147483647;
        a[k] = (h + x) & 255;
        if ((h & 4095) === 0) {
            h = h + 1;
        }
    }
    return h;
}

var iterations = 2000000;
var start = Date.now();
var result = bench(iterations);
var elapsed = Date.now() - start;
// The shell has no console; it prints the completion value instead.
"result=" + result + " iterations=" + iterations + " ms=" + elapsed +
    " iterPerSec=" + Math.round(iterations / (elapsed / 1000));
