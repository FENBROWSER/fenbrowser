// See https://aka.ms/new-console-template for more information
Console.WriteLine("Hello, World!");
using System;
using System.IO;
using System.Net.Http;

class Program {
    static void Main() {
        var html = ""
<!DOCTYPE html><html><head>
<script>var x = 1;</script>
<script nonce="pduU0v0cSRQlIv2mOuNWTAQ">(function(){console.log('test');})();</script>
</head><body></body></html>
";
        Console.WriteLine(html.Substring(0, 50));
    }
}
