// What a pre-sized array costs.
//
// `new Array(n)` and `a.length = n` set a length above the element count, and
// every index between them is a hole. Before the vector could represent that,
// either one materialised the array before it held a single element - and every
// read of it afterwards built a string from the index and looked it up. On
// reCAPTCHA's bundle 7,505 arrays were in that state.
//
// `literal` is the control: an array that was always dense, which must not move.
function build(n) { var a = []; for (var i = 0; i < n; i++) a[i] = i; return a; }
function prealloc(n) { var a = new Array(n); for (var i = 0; i < n; i++) a[i] = i; return a; }
function grown(n) { var a = []; a.length = n; for (var i = 0; i < n; i++) a[i] = i; return a; }

var lit = build(64), pre = prealloc(64), grw = grown(64);

function readLit(n) { var t = 0; for (var i = 0; i < n; i++) t += lit[i & 63]; return t; }
function readPre(n) { var t = 0; for (var i = 0; i < n; i++) t += pre[i & 63]; return t; }
function readGrw(n) { var t = 0; for (var i = 0; i < n; i++) t += grw[i & 63]; return t; }
function fillPre(n) { var t = 0; for (var i = 0; i < n / 64; i++) t += prealloc(64)[63]; return t; }
function fillLit(n) { var t = 0; for (var i = 0; i < n / 64; i++) t += build(64)[63]; return t; }

var rungs = [["read literal", readLit], ["read new Array(n)", readPre], ["read length=n", readGrw],
             ["build literal", fillLit], ["build new Array(n)", fillPre]];
for (var r = 0; r < rungs.length; r++) rungs[r][1](200000);
var N = 2000000, out = "";
for (var r = 0; r < rungs.length; r++) {
    var best = 1e18;
    for (var k = 0; k < 3; k++) { var t0 = Date.now(); rungs[r][1](N); var ms = Date.now() - t0; if (ms < best) best = ms; }
    out += rungs[r][0] + "=" + (best * 1e6 / N).toFixed(0) + "ns ";
}
out;
