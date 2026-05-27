function counter() {
  var n = 0;
  return function() { return ++n; };
}
var c = counter();
c(); c(); c();
return c();
