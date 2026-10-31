using System;
using System.Linq;

namespace Veldrid
{
    /// <summary>
    ///     Describes a <see cref="ResourceSet" />, for creation using a <see cref="ResourceFactory" />.
    /// </summary>
    public struct ResourceSetDescription : IEquatable<ResourceSetDescription>
    {
        /// <summary>
        ///     The <see cref="ResourceLayout" /> describing the number and kind of resources used.
        /// </summary>
        public ResourceLayout Layout;

        /// <summary>
        ///     An array of <see cref="IBindableResource" /> objects.
        ///     The number and type of resources must match those specified in the <see cref="ResourceLayout" />.
        /// </summary>
        public IBindableResource[] BoundResources;

        /// <summary>Set when built from slot bindings: per offset-selectable binding (in binding order), the arena that maps a <see cref="UniformArenaElement"/> to a byte offset.</summary>
        internal IUniformArenaOffsets[] VerifiedOffsetArenas;

        /// <summary>
        ///     Constructs a new ResourceSetDescription from NAMED bindings - the SAME call as before, with each positional resource
        ///     replaced by <c>slot.With(resource)</c> (BUGFIX_ShaderInterfaceContract_TypedSlotArguments.md). The layout must have been built
        ///     from the same shape's slots; every slot of its set is supplied exactly once; order is irrelevant.
        /// </summary>
        /// <param name="layout">A <see cref="ResourceLayout" /> created from <see cref="ResourceLayoutDescription(ShapeSlot[])"/>.</param>
        /// <param name="bindings">One <c>slot.With(...)</c> per slot of the layout's set.</param>
        public ResourceSetDescription(ResourceLayout layout, params ShapeResourceBinding[] bindings)
        {
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(bindings);
            var shape = layout.VerifiedShape
                ?? throw new ShaderContractException("resource set", [new ShaderContractViolation("BIND", "layout", "raw layout", "a layout built from shape slots", "sets are described by slot bindings against a slot-built layout")]);
            shape.EnsureBound();
            uint set = layout.VerifiedSetIndex;
            var violations = new System.Collections.Generic.List<ShaderContractViolation>();
            foreach (var binding in bindings)
            {
                if (binding.Slot is null) violations.Add(new ShaderContractViolation("BIND", "?", "default ShapeResourceBinding", "a slot's With(...)", "bindings must come from a slot"));
                else if (!ReferenceEquals(binding.Slot.Owner, shape)) violations.Add(new ShaderContractViolation("BIND", binding.Slot.Name, $"slot of {binding.Slot.Owner?.GetType().Name}", shape.GetType().Name, "a set binds only the layout's shape"));
                else if (binding.Slot.Set != set) violations.Add(new ShaderContractViolation("BIND", binding.Slot.Name, $"set {binding.Slot.Set}", $"set {set}", "a set binds only slots of the layout's descriptor set"));
            }
            if (violations.Count > 0) throw new ShaderContractException(shape.BundleName, violations);
            var elements = shape.CompiledLayoutFor(set).Elements ?? Array.Empty<ResourceLayoutElementDescription>();
            var bound = new IBindableResource[elements.Length];
            var arenas = new IUniformArenaOffsets[elements.Length];
            foreach (var binding in bindings)
            {
                int b = (int)binding.Slot.Binding;
                if (bound[b] != null) violations.Add(new ShaderContractViolation("BIND", binding.Slot.Name, "bound twice", "once", "each slot is supplied exactly once"));
                bound[b] = binding.Resource; arenas[b] = binding.Arena;
            }
            foreach (var slot in shape.SlotsOfSet(set))
                if (bound[slot.Binding] is null) violations.Add(new ShaderContractViolation("BIND", slot.Name, "not supplied", $"set {set} binding {slot.Binding} {elements[slot.Binding].Kind}", "every shader resource of the set must be bound"));
            if (violations.Count > 0) throw new ShaderContractException(shape.BundleName, violations);
            Layout = layout;
            BoundResources = bound;
            // Dynamic offsets are applied in BINDING order of the offset-selectable elements (same rule as the raw API).
            VerifiedOffsetArenas = shape.SlotsOfSet(set)
                .Where(s => s.UniformBindingOffsetSelectable).OrderBy(s => s.Binding)
                .Select(s => arenas[s.Binding]).ToArray();
        }

        /// <summary>
        ///     Positional construction (binding = array index). Internal: position is never evidence of agreement with the shader; the
        ///     public form takes slot bindings.
        /// </summary>
        internal ResourceSetDescription(ResourceLayout layout, params IBindableResource[] boundResources)
        {
            Layout = layout;
            BoundResources = boundResources;
            VerifiedOffsetArenas = Array.Empty<IUniformArenaOffsets>();
        }

        /// <summary>
        ///     Element-wise equality.
        /// </summary>
        /// <param name="other">The instance to compare to.</param>
        /// <returns>True if all elements and all array elements are equal; false otherswise.</returns>
        public bool Equals(ResourceSetDescription other)
        {
            return Layout.Equals(other.Layout) && Util.ArrayEquals(BoundResources, other.BoundResources);
        }

        /// <summary>
        ///     Returns the hash code for this instance.
        /// </summary>
        /// <returns>A 32-bit signed integer that is the hash code for this instance.</returns>
        public override int GetHashCode()
        {
            return HashHelper.Combine(Layout.GetHashCode(), HashHelper.Array(BoundResources));
        }
    }
}
