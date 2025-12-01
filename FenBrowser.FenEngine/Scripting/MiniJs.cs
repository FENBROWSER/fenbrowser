namespace FenBrowser.FenEngine.Scripting.MiniJs
{
    public enum JsType
    {
        Undefined = 0,
        Null = 1,
        Boolean = 2,
        Number = 3,
        String = 4,
        Object = 5,
        Function = 6,
        Array = 7
    }

    public sealed class JsValue
    {
        private JsType _type;
        private object _val;

        public JsType Type
        {
            get { return _type; }
        }

        private JsValue() { }

        public static JsValue Undefined()
        {
            return new JsValue { _type = JsType.Undefined, _val = null };
        }

        public static JsValue Null()
        {
            return new JsValue { _type = JsType.Null, _val = null };
        }

        public static JsValue Boolean(bool b)
        {
            return new JsValue { _type = JsType.Boolean, _val = b };
        }

        public static JsValue Number(double n)
        {
            return new JsValue { _type = JsType.Number, _val = n };
        }

        public static JsValue String(string s)
        {
            return new JsValue { _type = JsType.String, _val = s ?? "" };
        }

        public static JsValue ObjLit()
        {
            return new JsValue { _type = JsType.Object, _val = new System.Collections.Generic.Dictionary<string, JsValue>() };
        }

        public static JsValue Func(JsFunction fn)
        {
            return new JsValue { _type = JsType.Function, _val = fn };
        }

        public static JsValue Arr()
        {
            return new JsValue { _type = JsType.Array, _val = new System.Collections.Generic.List<JsValue>() };
        }

        public bool AsBoolean()
        {
            if (_type == JsType.Boolean)
                return (bool)_val;
            return false;
        }

        public double AsNumber()
        {
            if (_type == JsType.Number)
                return (double)_val;
            return 0.0;
        }

        public string AsString()
        {
            if (_val == null)
                return "";
            return _val.ToString();
        }
    }

    public sealed class JsFunction
    {
        public delegate JsValue NativeFunc(System.Collections.Generic.List<JsValue> args);
        public NativeFunc Native { get; set; }

        public JsFunction(NativeFunc native)
        {
            Native = native;
        }
    }

    public sealed class Engine
    {
        private System.Collections.Generic.Dictionary<string, JsValue> _globals;

        public Engine()
        {
            _globals = new System.Collections.Generic.Dictionary<string, JsValue>(System.StringComparer.OrdinalIgnoreCase);
        }

        public JsValue Execute(string code, System.Collections.Generic.Dictionary<string, JsValue> globals = null)
        {
            try
            {
                if (globals != null)
                    _globals = globals;
                return JsValue.Undefined();
            }
            catch
            {
                return JsValue.Undefined();
            }
        }

        public JsValue Invoke(JsFunction fn, System.Collections.Generic.List<JsValue> args)
        {
            try
            {
                if (fn != null && fn.Native != null)
                    return fn.Native(args ?? new System.Collections.Generic.List<JsValue>());
                return JsValue.Undefined();
            }
            catch
            {
                return JsValue.Undefined();
            }
        }

        public JsValue GetGlobal(string name)
        {
            JsValue result;
            if (_globals.TryGetValue(name, out result))
                return result;
            return JsValue.Undefined();
        }

        public void SetGlobal(string name, JsValue value)
        {
            if (_globals != null)
                _globals[name] = value;
        }
    }

    public static class Builtins
    {
        public static System.Collections.Generic.Dictionary<string, JsValue> Create(object jsEngine, object ctx)
        {
            var globals = new System.Collections.Generic.Dictionary<string, JsValue>(System.StringComparer.OrdinalIgnoreCase);
            globals["undefined"] = JsValue.Undefined();
            globals["null"] = JsValue.Null();
            var consoleObj = JsValue.ObjLit();
            globals["console"] = consoleObj;
            return globals;
        }
    }

    public static class Bootstrap
    {
        public static void InitEnvironment(Engine engine, System.Collections.Generic.Dictionary<string, JsValue> globals)
        {
            if (engine == null || globals == null)
                return;
        }
    }
}
