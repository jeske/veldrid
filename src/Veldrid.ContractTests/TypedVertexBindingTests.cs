using System;
using System.Linq;
using System.Reflection;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

public sealed class TypedVertexBindingTests
{
    [Fact]
    public void SetVertexBufferAcceptsOnlyTheInputsExactVertexType()
    {
        // Same method name as the raw API; the uint slot is replaced by VertexInput<T> and the buffer by ShaderVertexBuffer<T> (same T).
        var typed = typeof(CommandList).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "SetVertexBuffer" && m.IsGenericMethodDefinition).ToArray();
        Assert.NotEmpty(typed);
        foreach (var m in typed)
        {
            var p = m.GetParameters();
            Assert.Equal(typeof(VertexInput<>), p[0].ParameterType.GetGenericTypeDefinition());
            Assert.Equal(typeof(ShaderVertexBuffer<>), p[1].ParameterType.GetGenericTypeDefinition());
            Assert.Equal(p[0].ParameterType.GetGenericArguments()[0], p[1].ParameterType.GetGenericArguments()[0]);
        }
        Assert.DoesNotContain(typeof(VertexInput<ImGuiVertex>).GetMethods(BindingFlags.Public | BindingFlags.Instance), m => m.Name == "Bind");
    }

    [Fact]
    public void TypedBufferHasNoPublicRawBufferOrConstructor()
    {
        var type = typeof(ShaderVertexBuffer<ImGuiVertex>);
        Assert.Empty(type.GetConstructors());
        Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), p => p.PropertyType == typeof(DeviceBuffer));
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.Instance), m => m.Name == "Update");   // writes go through cl.UpdateBuffer
    }

    [Fact]
    public void UnboundInputCannotCreateBuffers()
    {
        var input = new VertexInput<ImGuiVertex>();
        Assert.Throws<InvalidOperationException>(() => input.CreateBuffer(1, BufferUsage.VertexBuffer));
    }
}