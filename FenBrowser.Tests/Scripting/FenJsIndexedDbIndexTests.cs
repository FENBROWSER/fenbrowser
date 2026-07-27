using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

public sealed class FenJsIndexedDbIndexTests
{
    [Fact]
    public async Task CreateIndexReturnsIndexAndPreservesExceptionOrder()
    {
        var baseUri = new Uri("https://fixture.test/indexeddb-create-index.html");
        var document = new HtmlParser(
            "<html><body><script>" +
            "globalThis.__indexResult='pending';" +
            "var open=indexedDB.open('create-index-shape',1);" +
            "var upgradeStore;" +
            "open.onupgradeneeded=function(e){" +
            "upgradeStore=open.result.createObjectStore('store');" +
            "var index=upgradeStore.createIndex('byValue','value',{unique:true});" +
            "var duplicate='none';" +
            "try{upgradeStore.createIndex('byValue','invalid key path');}catch(error){" +
            "duplicate=error.name+':'+error.code;" +
            "}" +
            "var deleted=open.result.createObjectStore('deleted');" +
            "open.result.deleteObjectStore('deleted');" +
            "var deletedError='none';" +
            "try{deleted.createIndex('index','value');}catch(error){" +
            "deletedError=error.name+':'+error.code;" +
            "}" +
            "globalThis.__indexShape=String(index instanceof IDBIndex)+':'+" +
            "index.name+':'+index.keyPath+':'+String(index.unique)+':'+" +
            "String(index.multiEntry)+':'+String(index.objectStore===upgradeStore)+':'+" +
            "duplicate+':'+deletedError;" +
            "};" +
            "open.onsuccess=function(){" +
            "var inactive='none';" +
            "try{upgradeStore.createIndex('late','value');}catch(error){" +
            "inactive=error.name+':'+error.code;" +
            "}" +
            "globalThis.__indexResult=globalThis.__indexShape+':'+inactive;" +
            "};" +
            "</script></body></html>",
            baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        Assert.True(SpinWait.SpinUntil(
            () => !string.Equals(
                engine.Evaluate("String(globalThis.__indexResult)")?.ToString(),
                "pending",
                StringComparison.Ordinal),
            millisecondsTimeout: 1000));
        Assert.Equal(
            "true:byValue:value:true:false:true:ConstraintError:0:InvalidStateError:11:TransactionInactiveError:0",
            engine.Evaluate("String(globalThis.__indexResult)")?.ToString());
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
