// Mirrors the *call* shape the reCAPTCHA bundle runs, which the existing
// obfuscated-VM benchmark deliberately leaves out: that one is a tight numeric
// loop with no calls at all, and the real page averages 58 bytecode
// instructions per activation (79M instructions across 1.37M frames). A
// workload that calls that often is bound by what an activation costs, not by
// what the dispatch loop costs, so it needs its own benchmark.
//
// Measured shape of the real job this imitates:
//   LoadVar 24%, LoadConst 16%, Move 11%, JumpIfFalse 6%, GetElem 6%,
//   StoreVar 4%, GetPropByName 3%, calls ~2.5%, the rest bitwise/compare.
// Dispatch through a table of small handlers, values carried on an object and
// an array, arguments passed positionally — the shape minified interpreter
// bundles compile to.

function makeMachine() {
    var state = { acc: 0, ptr: 0, flag: 0, depth: 0 };
    var mem = new Array(256);
    for (var i = 0; i < 256; i++) {
        mem[i] = (i * 7 + 1) & 255;
    }

    function opLoad(s, m, a, b, c, d) {
        var k = (a + b) & 255;
        s.ptr = k;
        return m[k] + (c & 1) + (d & 1);
    }

    function opMix(s, m, a, b, c, d) {
        var x = a ^ (b << 3);
        var y = (x >>> 5) | (c & 15);
        s.flag = (y & 1);
        return (x + y + d) & 65535;
    }

    function opStore(s, m, a, b, c, d) {
        var k = (s.ptr + a) & 255;
        m[k] = (b + c + d) & 255;
        return m[k];
    }

    function opBranch(s, m, a, b, c, d) {
        if ((a & 7) === 0) {
            return opMix(s, m, b, c, d, a);
        }
        if ((a & 3) === 1) {
            return opLoad(s, m, b, c, d, a);
        }
        return opStore(s, m, b, c, d, a);
    }

    var table = [opLoad, opMix, opStore, opBranch];

    return function step(n) {
        var s = state;
        var m = mem;
        var acc = 0;
        for (var i = 0; i < n; i++) {
            var op = table[i & 3];
            acc = op(s, m, acc & 255, (i >> 2) & 255, i & 15, s.flag);
            s.acc = acc;
            if ((acc & 1023) === 0) {
                acc = acc + s.ptr;
            }
        }
        return acc + s.acc + s.ptr + s.flag;
    };
}

var step = makeMachine();
var iterations = 1500000;
var start = Date.now();
var result = step(iterations);
var elapsed = Date.now() - start;
'bench_call_shape result=' + result + ' iterations=' + iterations + ' ms=' + elapsed;
