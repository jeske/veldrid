namespace Veldrid
{
    /// <summary>
    ///     One process's GPU memory accounting for one adapter, as reported by the OS (see
    ///     <see cref="GraphicsDevice.TryQueryVideoMemoryUsage" />). Bytes. LOCAL = the adapter's dedicated memory (VRAM on a
    ///     discrete GPU); NON-LOCAL = shared system memory the GPU can address (CPU-visible staging/upload buffers, driver
    ///     scratch, and VRAM overflow when the local budget is exceeded). BUDGET is what the OS currently lets this process use
    ///     before it starts paging/demoting; usage above budget is the OS's definition of "over".
    /// </summary>
    public readonly struct GraphicsDevice_VideoMemoryUsage
    {
        public readonly ulong LocalUsageBytes;
        public readonly ulong LocalBudgetBytes;
        public readonly ulong NonLocalUsageBytes;
        public readonly ulong NonLocalBudgetBytes;

        public GraphicsDevice_VideoMemoryUsage(ulong localUsageBytes, ulong localBudgetBytes, ulong nonLocalUsageBytes, ulong nonLocalBudgetBytes)
        {
            LocalUsageBytes = localUsageBytes;
            LocalBudgetBytes = localBudgetBytes;
            NonLocalUsageBytes = nonLocalUsageBytes;
            NonLocalBudgetBytes = nonLocalBudgetBytes;
        }
    }
}