namespace FenBrowser.Js.Environments;

// Extension surface required by GlobalEnvironmentRecord on top of plain IBindingObject.
//
// The base IBindingObject only models "name -> value" lookups, which is everything an
// ObjectEnvironmentRecord needs. The global environment additionally needs to inspect
// extensibility and own-property attributes to implement HasRestrictedGlobalProperty,
// CanDeclareGlobalVar and CanDeclareGlobalFunction (ECMA-262 9.1.1.4.14-16).
//
// Splitting these methods out of IBindingObject keeps with-statement / object-env
// tests free of fake-implementation churn whenever the global surface evolves.
public interface IGlobalObject : IBindingObject
{
    // ECMA-262 [[Extensible]] - false once Object.preventExtensions / Object.freeze
    // has been applied. Declarations that would add a new own property must respect
    // this.
    bool IsExtensible { get; }

    // True iff the property exists directly on this object (no prototype walking).
    // 9.1.1.4 routinely distinguishes own from inherited.
    bool HasOwnProperty(string name);

    // True when no own property exists for this name, or when the own property has
    // Configurable=true. False only when an own non-configurable descriptor exists.
    // Translates 9.1.1.4.14 HasRestrictedGlobalProperty into a single call.
    bool IsOwnPropertyConfigurable(string name);

    // True iff this name resolves to an own data property with both Writable and
    // Enumerable set. Required by 9.1.1.4.16 CanDeclareGlobalFunction step 4 ("If
    // IsDataDescriptor(existingProp) is true and existingProp has attribute values
    // {[[Writable]]: true, [[Enumerable]]: true}, return true"). Returns false for
    // accessor descriptors, missing names, and any data descriptor that is read-only
    // or non-enumerable.
    bool IsOwnDataPropertyWritableEnumerable(string name);
}
