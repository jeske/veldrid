using System;
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
    public void UnconstructedShapeCannotCreatePipeline()
    {
        var shape = new ImGuiShape();
        Assert.Throws<InvalidOperationException>(() => shape.CreateGraphicsPipeline(default, default, default, default, default));
        shape.Dispose();
        Assert.Throws<ObjectDisposedException>(() => shape.CreateGraphicsPipeline(default, default, default, default, default));
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
        float value = 1;
        Assert.Throws<ArgumentException>(() => block.UpdateMember(null, x => captured.Value, in value));
    }

    [Fact]
    public void MemberSelectorCannotFlattenNestedField()
    {
        var block = new UniformBlock<Nested>();
        float value = 1;
        Assert.Throws<ArgumentException>(() => block.UpdateMember(null, x => x.Inner.Value, in value));
    }

    [Fact]
    public void UnboundUniformRejectsDirectMemberWrite()
    {
        var block = new UniformBlock<Values>();
        float value = 1;
        Assert.Throws<InvalidOperationException>(() => block.UpdateMember(null, x => x.Value, in value));
    }

    [Theory]
    [InlineData("CreateShader")]
    [InlineData("CreateGraphicsPipeline")]
    [InlineData("CreateComputePipeline")]
    [InlineData("CreateResourceSet")]
    [InlineData("CreateResourceLayout")]
    public void RawFactoryMethodsAreNotPublic(string name)
        => Assert.DoesNotContain(typeof(ResourceFactory).GetMethods(BindingFlags.Public | BindingFlags.Instance), m => m.Name == name);

    [Theory]
    [InlineData("SetVertexBuffer")]
    [InlineData("SetGraphicsResourceSet")]
    [InlineData("SetComputeResourceSet")]
    public void NumberedBindersAreNotPublic(string name)
        => Assert.DoesNotContain(typeof(CommandList).GetMethods(BindingFlags.Public | BindingFlags.Instance), m => m.Name == name);
}