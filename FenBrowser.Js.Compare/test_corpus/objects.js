var obj = { a: 1, b: 2, c: 3 };
var sum = 0;
for (var k in obj) sum += obj[k];
return sum + '/' + Object.keys(obj).length;
