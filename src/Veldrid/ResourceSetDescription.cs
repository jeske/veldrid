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
        ///     Consumers build transient sets per draw (clip masks, filters), so the happy path allocates only the two arrays the set
        ///     itself owns; the violation list exists only once a violation has been found.
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
            System.Collections.Generic.List<ShaderContractViolation> violations = null;
            foreach (var binding in bindings)
            {
                if (binding.Slot is null) (violations ??= new()).Add(new ShaderContractViolation("BIND", "?", "default ShapeResourceBinding", "a slot's With(...)", "bindings must come from a slot"));
                else if (!ReferenceEquals(binding.Slot.Owner, shape)) (violations ??= new()).Add(new ShaderContractViolation("BIND", binding.Slot.Name, $"slot of {binding.Slot.Owner?.GetType().Name}", shape.GetType().Name, "a set binds only the layout's shape"));
                else if (binding.Slot.Set != set) (violations ??= new()).Add(new ShaderContractViolation("BIND", binding.Slot.Name, $"set {binding.Slot.Set}", $"set {set}", "a set binds only slots of the layout's descriptor set"));
            }
            if (violations is not null) throw new ShaderContractException(shape.BundleName, violations);
            var elements = shape.CompiledLayoutFor(set).Elements ?? Array.Empty<ResourceLayoutElementDescription>();
            var bound = new IBindableResource[elements.Length];
            // Offset-selectable bindings are a fixed property of the compiled set: count them once so the arena array is exact-sized
            // (Array.Empty for the common no-arena set) and filled in BINDING order without sorting (same rule as the raw API).
            int offsetSelectableCount = 0;
            foreach (var slot in shape.SlotsOfSetSpan(set))
                if (slot.UniformBindingOffsetSelectable) offsetSelectableCount++;
            var arenasInBindingOrder = offsetSelectableCount == 0 ? Array.Empty<IUniformArenaOffsets>() : new IUniformArenaOffsets[offsetSelectableCount];
            foreach (var binding in bindings)
            {
                int b = (int)binding.Slot.Binding;
                if (bound[b] != null) (violations ??= new()).Add(new ShaderContractViolation("BIND", binding.Slot.Name, "bound twice", "once", "each slot is supplied exactly once"));
                bound[b] = binding.Resource;
            }
            foreach (var slot in shape.SlotsOfSetSpan(set))
                if (bound[slot.Binding] is null) (violations ??= new()).Add(new ShaderContractViolation("BIND", slot.Name, "not supplied", $"set {set} binding {slot.Binding} {elements[slot.Binding].Kind}", "every shader resource of the set must be bound"));
            if (violations is not null) throw new ShaderContractException(shape.BundleName, violations);
            // Second pass over the bindings, ordered by binding index via the slot list (already binding-ordered per set in the bundle),
            // so each offset-selectable binding's arena lands at its position among the offset-selectable bindings.
            int arenaIndex = 0;
            foreach (var slot in shape.SlotsOfSetSpan(set))
            {
                if (!slot.UniformBindingOffsetSelectable) continue;
                arenasInBindingOrder[arenaIndex++] = ArenaBoundTo(bindings, slot);
            }
            Layout = layout;
            BoundResources = bound;
            VerifiedOffsetArenas = arenasInBindingOrder;
        }

        /// <summary>The arena supplied for <paramref name="slot"/> (every slot of the set is bound exactly once by the time this runs).</summary>
        private static IUniformArenaOffsets ArenaBoundTo(ShapeResourceBinding[] bindings, ShapeSlot slot)
        {
            foreach (var binding in bindings)
                if (ReferenceEquals(binding.Slot, slot)) return binding.Arena;
            return null;
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
