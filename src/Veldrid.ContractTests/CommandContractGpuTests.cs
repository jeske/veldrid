using System;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>Bounded, windowless checks of the real command-list entry points, not just the validation helper.</summary>
public sealed class CommandContractGpuTests
{
    [Fact]
    public void D3D11_DrawAndDispatchWithoutVerifiedPipelineThrowBeforeNativeCommands()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions());
        using var commands = device.ResourceFactory.CreateCommandList();
        commands.Begin();
        Assert.Throws<ShaderContractException>(() => commands.Draw(3));
        Assert.Throws<ShaderContractException>(() => commands.Dispatch(1, 1, 1));
        using var indirect = device.ResourceFactory.CreateBuffer(new BufferDescription(12, BufferUsage.IndirectBuffer));
        Assert.Throws<ShaderContractException>(() => commands.DispatchIndirect(indirect, 0));
        commands.End();
    }
}