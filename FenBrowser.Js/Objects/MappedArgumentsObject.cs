using System.Globalization;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

/// <summary>
/// ECMA-262 10.4.4 the arguments exotic object, in its mapped form
/// (CreateMappedArgumentsObject, 10.4.4.7): while an index is mapped, reading
/// it reads the parameter's binding and writing it writes the binding, in both
/// directions - <c>function f(a) { a = 2; return arguments[0]; }</c> answers 2.
/// </summary>
/// <remarks>
/// A mapped index is held outside the shape. Its value is the binding's, which
/// changes without the object hearing of it, so a property cache that read it
/// from a slot would answer with a stale value; kept out of the shape, no cache
/// can guard on it, and <see cref="MayGainOwnPropertyOutsideShape"/> stops one
/// answering from the prototype instead. An index leaves the map - and, unless
/// deleted, moves into the shape as an ordinary property - when it is deleted,
/// redefined as an accessor or made non-writable (10.4.4.2, 10.4.4.5).
/// </remarks>
public sealed class MappedArgumentsObject : JsObject
{
    private readonly DeclarativeEnvironmentRecord _environment;

    // The callee's formals and their slots in the record (-1: found by name),
    // shared with the function rather than copied per call.
    private readonly IReadOnlyList<string> _parameterNames;
    private readonly int[] _parameterSlots;

    // Per index below the mapped count. A mapped property is always a
    // writable data property; its other two attributes can still change
    // without unmapping it.
    private readonly byte[] _state;

    private const byte Mapped = 1;
    private const byte Enumerable = 2;
    private const byte Configurable = 4;

    /// <param name="environment">The record holding the parameters.</param>
    /// <param name="parameterNames">The formal parameters, in order.</param>
    /// <param name="parameterSlots">Each parameter's slot in the record, or -1; empty when the record holds them by name.</param>
    /// <param name="argumentCount">How many arguments the call passed.</param>
    public MappedArgumentsObject(
        DeclarativeEnvironmentRecord environment,
        IReadOnlyList<string> parameterNames,
        int[] parameterSlots,
        int argumentCount)
    {
        _environment = environment;
        _parameterNames = parameterNames;
        _parameterSlots = parameterSlots;
        // The object reaches the record for as long as it lives, so the call's
        // teardown must not reclaim the record's storage.
        EnvironmentRecord.MarkEscapedChain(environment);

        _state = new byte[Math.Min(argumentCount, parameterNames.Count)];
        for (var index = 0; index < _state.Length; index++)
        {
            // 10.4.4.7 steps 15-17: of two parameters with the same name only
            // the later one is mapped. Duplicate names are rare and formals
            // few, so a scan beats building a set on every call.
            var name = parameterNames[index];
            var shadowed = false;
            for (var later = index + 1; later < parameterNames.Count && !shadowed; later++)
            {
                shadowed = string.Equals(parameterNames[later], name, StringComparison.Ordinal);
            }

            if (!shadowed)
            {
                _state[index] = Mapped | Enumerable | Configurable;
            }
        }
    }

    /// <summary>
    /// Whether the index is mapped. Every other argument index is the
    /// creator's to define as an ordinary property (10.4.4.7 step 14).
    /// </summary>
    internal bool IsMapped(int index) =>
        (uint)index < (uint)_state.Length && (_state[index] & Mapped) != 0;

    public override bool MayGainOwnPropertyOutsideShape(string key)
        => TryMappedIndex(key, out _) || base.MayGainOwnPropertyOutsideShape(key);

    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        // 10.4.4.1 [[GetOwnProperty]]: a mapped index reads the binding.
        if (TryMappedIndex(key, out var index))
        {
            descriptor = Describe(index);
            return true;
        }

        return base.TryGetOwnProperty(key, out descriptor);
    }

    // 10.4.4.2 [[DefineOwnProperty]]. The caller has already validated the
    // descriptor against the current one and completed it from it.
    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        if (!TryMappedIndex(key, out var index))
        {
            return base.DefineOwnProperty(key, descriptor);
        }

        var state = _state[index];
        if ((state & Configurable) == 0 &&
            (descriptor.Configurable ||
             descriptor.IsAccessor ||
             (descriptor.HasEnumerable && descriptor.Enumerable != ((state & Enumerable) != 0))))
        {
            return false;
        }

        if (descriptor.IsAccessor)
        {
            // Step 7.a: an accessor ends the mapping.
            Unmap(index);
            return MoveIntoShape(key, descriptor);
        }

        if (descriptor.HasValue)
        {
            // Step 7.b.i: the value goes to the binding.
            WriteBinding(index, descriptor.Value);
        }

        var enumerable = descriptor.HasEnumerable ? descriptor.Enumerable : (state & Enumerable) != 0;
        var configurable = descriptor.HasConfigurable ? descriptor.Configurable : (state & Configurable) != 0;
        if (descriptor.HasWritable && !descriptor.Writable)
        {
            // Steps 6 and 7.b.ii: made read-only, the property keeps the value
            // the binding has now and leaves the map.
            var frozen = ReadBinding(index);
            Unmap(index);
            return MoveIntoShape(key, new JsPropertyDescriptor(frozen, Writable: false, enumerable, configurable));
        }

        _state[index] = (byte)(Mapped | (enumerable ? Enumerable : 0) | (configurable ? Configurable : 0));
        return true;
    }

    // 10.4.4.4 [[Set]]: with the object as its own receiver, a mapped index
    // writes the binding.
    public override bool SetProperty(string key, JsValue value)
    {
        if (TryMappedIndex(key, out var index))
        {
            WriteBinding(index, value);
            return true;
        }

        return base.SetProperty(key, value);
    }

    // 10.4.4.5 [[Delete]].
    public override bool DeleteProperty(string key)
    {
        if (TryMappedIndex(key, out var index))
        {
            if ((_state[index] & Configurable) == 0)
            {
                return false;
            }

            Unmap(index);
            return true;
        }

        return base.DeleteProperty(key);
    }

    // 10.1.11.1 OrdinaryOwnPropertyKeys: the mapped indices take their places
    // among the integer keys, which come first and in ascending order.
    public override IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        var mapped = 0;
        foreach (var pair in base.EnumerateOwnProperties())
        {
            if (IsArrayIndexKey(pair.Key, out var ordinaryIndex))
            {
                for (; mapped < _state.Length && mapped < ordinaryIndex; mapped++)
                {
                    if (TryDescribe(mapped, out var entry))
                    {
                        yield return entry;
                    }
                }
            }
            else
            {
                for (; mapped < _state.Length; mapped++)
                {
                    if (TryDescribe(mapped, out var entry))
                    {
                        yield return entry;
                    }
                }
            }

            yield return pair;
        }

        for (; mapped < _state.Length; mapped++)
        {
            if (TryDescribe(mapped, out var entry))
            {
                yield return entry;
            }
        }
    }

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        if (tracer.TraceEnvironmentChains)
        {
            _environment.Trace(tracer);
        }
    }

    private bool TryDescribe(int index, out KeyValuePair<string, JsPropertyDescriptor> entry)
    {
        if (!IsMapped(index))
        {
            entry = default;
            return false;
        }

        entry = new KeyValuePair<string, JsPropertyDescriptor>(
            index.ToString(CultureInfo.InvariantCulture), Describe(index));
        return true;
    }

    private JsPropertyDescriptor Describe(int index)
    {
        var state = _state[index];
        return new JsPropertyDescriptor(
            ReadBinding(index),
            Writable: true,
            Enumerable: (state & Enumerable) != 0,
            Configurable: (state & Configurable) != 0);
    }

    private bool TryMappedIndex(string key, out int index)
    {
        if (_state.Length != 0 &&
            IsArrayIndexKey(key, out var parsed) &&
            parsed < (uint)_state.Length &&
            (_state[parsed] & Mapped) != 0)
        {
            index = (int)parsed;
            return true;
        }

        index = -1;
        return false;
    }

    private void Unmap(int index) => _state[index] = 0;

    // 10.4.4.7.1 MakeArgGetter / MakeArgSetter: the parameter binding in the
    // function's record. A parameter binding is always initialized and mutable.
    private JsValue ReadBinding(int index)
    {
        if ((uint)index < (uint)_parameterSlots.Length && _parameterSlots[index] is var slot and >= 0 &&
            _environment.TryGetAtSlot(slot, out var value, out _))
        {
            return value;
        }

        _ = _environment.GetBindingValue(_parameterNames[index], strict: false, out value);
        return value;
    }

    private void WriteBinding(int index, JsValue value)
    {
        if ((uint)index < (uint)_parameterSlots.Length && _parameterSlots[index] is var slot and >= 0 &&
            _environment.TrySetAtSlot(slot, value, strict: false, out _))
        {
            return;
        }

        _ = _environment.SetMutableBinding(_parameterNames[index], value, strict: false);
    }
}
