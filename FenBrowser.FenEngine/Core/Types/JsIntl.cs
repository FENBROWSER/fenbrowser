using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core.Types
{
    public static class JsIntl
    {
        public static FenObject CreateIntlObject(IExecutionContext context)
        {
            var intl = new FenObject();
            intl.Set("DateTimeFormat", FenValue.FromFunction(CreateDateTimeFormat(context)));
            intl.Set("NumberFormat", FenValue.FromFunction(CreateNumberFormat(context)));
            intl.Set("Collator", FenValue.FromFunction(CreateCollator(context)));
            return intl;
        }

        private static FenFunction CreateDateTimeFormat(IExecutionContext context)
        {
            var ctor = new FenFunction("DateTimeFormat", (args, thisVal) =>
            {
                var locales = args.Length > 0 ? args[0] : FenValue.Undefined;
                var options = args.Length > 1 ? args[1] : FenValue.Undefined;
                
                // Parse locale
                CultureInfo culture = CultureInfo.CurrentCulture;
                try 
                {
                    if (locales.IsString) culture = new CultureInfo(locales.ToString());
                    else if (locales.IsObject) 
                    {
                        // Array of locales?
                        var arr = locales.AsObject();
                        // simplified: take first
                    }
                }
                catch {}

                var formatter = new FenObject();
                
                formatter.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) => 
                {
                    DateTime dt = DateTime.Now;
                    if (fArgs.Length > 0)
                    {
                        var arg = fArgs[0];
                        if (arg.IsNumber) dt = new DateTime(1970, 1, 1).AddMilliseconds(arg.ToNumber());
                        else if (arg.IsObject && (arg.AsObject() as FenObject)?.NativeObject is DateTime d) dt = d;
                    }
                    return FenValue.FromString(dt.ToString("d", culture)); // Simplified 'd' format
                })));
                
                formatter.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (rArgs, rThis) => 
                {
                    var opt = new FenObject();
                    opt.Set("locale", FenValue.FromString(culture.Name));
                    return FenValue.FromObject(opt);
                })));

                return FenValue.FromObject(formatter);
            });
            return ctor;
        }

        private static FenFunction CreateNumberFormat(IExecutionContext context)
        {
            var ctor = new FenFunction("NumberFormat", (args, thisVal) =>
            {
                var locales = args.Length > 0 ? args[0] : FenValue.Undefined;
                var options = args.Length > 1 ? args[1] : FenValue.Undefined;
                
                CultureInfo culture = CultureInfo.CurrentCulture;
                try { if (locales.IsString) culture = new CultureInfo(locales.ToString()); } catch {}

                bool isCurrency = false;
                string currency = "USD";
                
                if (options.IsObject)
                {
                    var opts = options.AsObject();
                    var style = opts.Get("style")?.ToString();
                    if (style == "currency") 
                    {
                        isCurrency = true;
                        currency = opts.Get("currency")?.ToString() ?? "USD";
                    }
                }

                var formatter = new FenObject();
                formatter.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) => 
                {
                    double num = fArgs.Length > 0 ? fArgs[0].ToNumber() : double.NaN;
                    if (double.IsNaN(num)) return FenValue.FromString("NaN");
                    
                    if (isCurrency) return FenValue.FromString(num.ToString("C", culture));
                    return FenValue.FromString(num.ToString("N", culture));
                })));
                
                formatter.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (rArgs, rThis) => 
                {
                    var opt = new FenObject();
                    opt.Set("locale", FenValue.FromString(culture.Name));
                    return FenValue.FromObject(opt);
                })));

                return FenValue.FromObject(formatter);
            });
            return ctor;
        }
        
        private static FenFunction CreateCollator(IExecutionContext context)
        {
               var ctor = new FenFunction("Collator", (args, thisVal) => 
               {
                   return FenValue.FromObject(new FenObject()); // Stub
               });
               return ctor;
        }
    }
}
