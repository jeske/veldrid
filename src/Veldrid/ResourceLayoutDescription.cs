using System;

namespace Veldrid
{
    /// <summary>
    ///     Describes the layout of <see cref="IBindableResource" /> objects for a <see cref="Pipeline" />.
    /// </summary>
    public struct ResourceLayoutDescription : IEquatable<ResourceLayoutDescription>
    {
        /// <summary>
        ///     An array of <see cref="ResourceLayoutElementDescription" /> objects, describing the properties of each resource
        ///     element in the <see cref="ResourceLayout" />.
        /// </summary>
        public ResourceLayoutElementDescription[] Elements;

        /// <summary>Set when built from shape slots: the shape and descriptor-set index every element belongs to.</summary>
        internal ShaderPipelineShape VerifiedShape;
        internal uint VerifiedSetIndex;

        /// <summary>
        ///     Constructs a new ResourceLayoutDescription from a shape's verified slots - the SAME call as before, with each positional
        ///     element replaced by the typed slot that names it (BUGFIX_ShaderInterfaceContract_TypedSlotArguments.md). All slots must
        ///     belong to one shape and one descriptor set, and every slot of that set must be present exactly once; order is irrelevant
        ///     because each slot carries its binding. Offset-selectable blocks come from the field's
        ///     <see cref="UniformBufferBindingTimeOffsetSelectableAttribute"/>, never from a hand-set option.
        /// </summary>
        /// <param name="slots">The shape's slots for one descriptor set.</param>
        public ResourceLayoutDescription(params ShapeSlot[] slots)
        {
            ArgumentNullException.ThrowIfNull(slots);
            if (slots.Length == 0) throw new ArgumentException("A resource layout needs at least one slot.", nameof(slots));
            var violations = new System.Collections.Generic.List<ShaderContractViolation>();
            foreach (var slot in slots)
            {
                if (slot is null) violations.Add(new ShaderContractViolation("LAYOUT", "?", "null", "a shape slot", "layout positions must be shape slots"));
                else if (slot.Owner is null) violations.Add(new ShaderContractViolation("LAYOUT", slot.Name, "unbound slot", "slot of a Bind()-created shape", "slots come from ShaderPipelineShape.Bind"));
            }
            if (violations.Count > 0) throw new ShaderContractException("resource layout", violations);
            var shape = slots[0].Owner;
            shape.EnsureBound();
            uint set = slots[0].Set;
            foreach (var slot in slots)
            {
                if (!ReferenceEquals(slot.Owner, shape)) violations.Add(new ShaderContractViolation("LAYOUT", slot.Name, $"slot of {slot.Owner.GetType().Name}", shape.GetType().Name, "one layout binds one shape's slots"));
                else if (slot.Set != set) violations.Add(new ShaderContractViolation("LAYOUT", slot.Name, $"set {slot.Set}", $"set {set}", "one layout spans one descriptor set"));
            }
            if (violations.Count > 0) throw new ShaderContractException(shape.BundleName, violations);
            var compiled = shape.CompiledLayoutFor(set);
            var seen = new bool[compiled.Elements.Length];
            foreach (var slot in slots)
            {
                if (seen[slot.Binding]) violations.Add(new ShaderContractViolation("LAYOUT", slot.Name, "listed twice", "once", "each slot appears exactly once"));
                seen[slot.Binding] = true;
            }
            foreach (var slot in shape.SlotsOfSet(set))
                if (!seen[slot.Binding]) violations.Add(new ShaderContractViolation("LAYOUT", slot.Name, "missing", $"set {set} binding {slot.Binding}", "every slot of the set must be listed"));
            if (violations.Count > 0) throw new ShaderContractException(shape.BundleName, violations);
            Elements = (ResourceLayoutElementDescription[])compiled.Elements.Clone();
            VerifiedShape = shape;
            VerifiedSetIndex = set;
        }

        /// <summary>
        ///     Positional construction (binding = array index). Internal: position is never evidence of agreement with the shader; the
        ///     public form takes shape slots.
        /// </summary>
        internal ResourceLayoutDescription(params ResourceLayoutElementDescription[] elements)
        {
            Elements = elements;
            VerifiedShape = null;
            VerifiedSetIndex = 0;
        }

        /// <summary>
        ///     Element-wise equality.
        /// </summary>
        /// <param name="other">The instance to compare to.</param>
        /// <returns>True if all array elements are equal; false otherswise.</returns>
        public bool Equals(ResourceLayoutDescription other)
        {
            return Util.ArrayEqualsEquatable(Elements, other.Elements);
        }

        /// <summary>
        ///     Returns the hash code for this instance.
        /// </summary>
        /// <returns>A 32-bit signed integer that is the hash code for this instance.</returns>
        public override int GetHashCode()
        {
            return HashHelper.Array(Elements);
        }
    }
}
