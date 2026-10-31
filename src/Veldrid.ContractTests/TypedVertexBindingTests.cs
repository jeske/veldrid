using System;
using System.Linq;
using System.Reflection;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

public sealed class TypedVertexBindingTests
{
    [Fact]
    public void BindAcceptsOnlyTheSlotsExactVertexType()
    {
        var bind = typeof(VertexInput<ImGuiVertex>).GetMethod("Bind")!;
        Assert.Equal(typeof(ShaderVertexBuffer<ImGuiVertex>), bind.GetParameters()[1].ParameterType);
        Assert.False(bind.IsGenericMethod);
        Assert.DoesNotContain(typeof(ShaderPipelineShape).GetMethods(), m => m.Name == "BindVertexBuffers");
    }

    [Fact]
    public void TypedBufferHasNoPublicRawBufferOrConstructor()
    {
        var type = typeof(ShaderVertexBuffer<ImGuiVertex>);
        Assert.Empty(type.GetConstructors());
        Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), p => p.PropertyType == typeof(DeviceBuffer));
        var update = type.GetMethod("Update")!;
        Assert.False(update.IsGenericMethod);
        Assert.Equal(typeof(ReadOnlySpan<ImGuiVertex>), update.GetParameters()[1].ParameterType);
    }

    [Fact]
    public void UnboundInputCannotCreateOrBindBuffers()
    {
        var input = new VertexInput<ImGuiVertex>();
        Assert.Throws<InvalidOperationException>(() => input.CreateBuffer(1));
        Assert.Throws<InvalidOperationException>(() => input.Bind(null!, null!));
    }
}