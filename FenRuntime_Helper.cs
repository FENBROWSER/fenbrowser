
        private FenValue ConvertNativeToFenValue(object obj)
        {
            if (obj == null) return FenValue.Null;
            if (obj is bool b) return FenValue.FromBoolean(b);
            if (obj is string s) return FenValue.FromString(s);
            if (obj is int i) return FenValue.FromNumber(i);
            if (obj is double d) return FenValue.FromNumber(d);
            if (obj is float f) return FenValue.FromNumber(f);
            if (obj is long l) return FenValue.FromNumber(l);
            if (obj is IObject io) return FenValue.FromObject(io);

            // Handle Dictionary as JS Object
            if (obj is System.Collections.IDictionary dict)
            {
                var fenObj = new FenObject();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    fenObj.Set(entry.Key.ToString(), ConvertNativeToFenValue(entry.Value));
                }
                return FenValue.FromObject(fenObj);
            }

            // Handle List/Array as JS Array (Object with length)
            if (obj is System.Collections.IEnumerable list)
            {
                var fenObj = new FenObject();
                int index = 0;
                foreach (var item in list)
                {
                    fenObj.Set(index.ToString(), ConvertNativeToFenValue(item));
                    index++;
                }
                fenObj.Set("length", FenValue.FromNumber(index));
                return FenValue.FromObject(fenObj);
            }

            return FenValue.Null;
        }
