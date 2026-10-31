using System;
using System.Linq;
using System.Reflection;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

public sealed class ShapeBypassTests
{
    public struct Values { public float Value; }
    public struct Nested { public Values Inner; }

    private sealed class SlotFields
    {
        public TextureSlot Albedo = new();
        public TextureSlot Normal = new();
    }

    [Fact]
    public void ReplacingSlotFieldFailsByName()
    {
        var fields = new SlotFields();
        var bindings = new (FieldInfo Field, object Handle)[]
        {
            (typeof(SlotFields).GetField(nameof(SlotFields.Albedo))!, fields.Albedo),
            (typeof(SlotFields).GetField(nameof(SlotFields.Normal))!, fields.Normal),
        };
        ShaderPipelineShape.ValidateFieldIdentity(fields, bindings);
        fields.Albedo = fields.Normal;
        var ex = Assert.Throws<ShaderContractException>(() => ShaderPipelineShape.ValidateFieldIdentity(fields, bindings));
        Assert.Equal("Albedo", Assert.Single(ex.Violations).Name);
    }

    [Fact]
    public void UnconstructedShapeSlotsCannotBeUsed()
    {
        var shape = new ImGuiShape();
        // Fields are null until Bind installs the verified handles; a layout cannot be described from them.
        Assert.Throws<ShaderContractException>(() => new ResourceLayoutDescription(shape.Projection, shape.FontSampler));
        shape.Dispose();
        Assert.Throws<ObjectDisposedException>(() => shape.EnsureBound());
    }

    [ShaderBundle("Invalid")]
    public class UnsealedShape : ShaderPipelineShape { }

    [Fact]
    public void ShapeDescriptorCannotBeOverridden()
    {
        var method = typeof(ShaderPipelineShape).GetMethod("Describe", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.True(method.IsPrivate);
        Assert.False(method.IsVirtual);
    }

    [Fact]
    public void UnsealedShapesFailBeforeMaterialization()
        => Assert.Throws<ShaderContractException>(() => ShapeDescriptorBuilder.FromReflection(typeof(UnsealedShape)));

    [Fact]
    public void MemberSelectorCannotUseCapturedObjectWithSameFieldName()
    {
        var captured = new Values();
        var block = new UniformBlock<Values>();
        Assert.Throws<ArgumentException>(() => block.ResolveMemberOffset<float>(x => captured.Value));
    }

    [Fact]
    public void MemberSelectorCannotFlattenNestedField()
    {
        var block = new UniformBlock<Nested>();
        Assert.Throws<ArgumentException>(() => block.ResolveMemberOffset<float>(x => x.Inner.Value));
    }

    [Fact]
    public void UnboundUniformRejectsDirectMemberWrite()
    {
        var block = new UniformBlock<Values>();
        Assert.Throws<InvalidOperationException>(() => block.ResolveMemberOffset<float>(x => x.Value));
    }

    [Theory]
    [InlineData("CreateShader")]
    public void RawFactoryMethodsAreNotPublic(string name)
        => Assert.DoesNotContain(typeof(ResourceFactory).GetMethods(BindingFlags.Public | BindingFlags.Instance), m => m.Name == name);

    /// <summary>The positional descriptions (binding = array index) are not constructible publicly; only the slot-typed constructors are.</summary>
    [Theory]
    [InlineData(typeof(ResourceLayoutDescription), typeof(ResourceLayoutElementDescription[]))]
    [InlineData(typeof(ResourceSetDescription), typeof(IBindableResource[]))]
    [InlineData(typeof(ShaderSetDescription), typeof(VertexLayoutDescription[]))]
    public void PositionalDescriptionConstructorsAreNotPublic(Type description, Type positionalArgument)
        => Assert.DoesNotContain(description.GetConstructors(BindingFlags.Public | BindingFlags.Instance),
            c => c.GetParameters().Any(p => p.ParameterType == positionalArgument));

    /// <summary>Same method names stay public; no public overload takes a uint slot/set index.</summary>
    [Theory]
    [InlineData("SetVertexBuffer")]
    [InlineData("SetGraphicsResourceSet")]
    [InlineData("SetComputeResourceSet")]
    public void NumberedBindersAreNotPublicButTypedSameNameFormsAre(string name)
    {
        var publicForms = typeof(CommandList).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == name).ToArray();
        Assert.NotEmpty(publicForms);
        Assert.DoesNotContain(publicForms, m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(uint));
    }
}