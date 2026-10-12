using ParagliderToolbox.Airflow.Simulation;

namespace ParagliderToolbox.Airflow.Gpu;

/// <summary>
/// The airflow solver on the graphics card (CUDA on NVIDIA cards, else OpenCL): the same algorithm as
/// <see cref="AirflowSolver"/>, many times faster on large grids. The card is found and the kernels compiled on first
/// use (a few seconds); ask from a background thread.
/// </summary>
/// <example>
/// <code>
/// using var simulation = new AirflowSimulation(terrain, colors, settings, solver: AirflowGpu.Factory);
/// var field = simulation.Run();
/// </code>
/// </example>
public static class AirflowGpu
{
    private static readonly Lazy<GpuDevice?> s_device = new(() => Open(cpu: false));
    private static readonly Lazy<GpuDevice?> s_cpu = new(() => Open(cpu: true));

    /// <summary>Gets whether there is a graphics card to run the solver on.</summary>
    public static bool IsAvailable => s_device.Value != null;

    /// <summary>Gets whether there is a graphics card without waiting for it to be looked for: <c>null</c> until then (see <see cref="IsAvailable"/>).</summary>
    public static bool? IsKnownAvailable => s_device.IsValueCreated ? s_device.Value != null : null;

    /// <summary>Gets the graphics card's name, or <c>null</c> without one.</summary>
    public static string? DeviceName => s_device.Value?.Name;

    /// <summary>Gets why there is no graphics card to use (no driver, or the error opening it), or <c>null</c> when there is one.</summary>
    public static string? UnavailableReason => IsAvailable ? null : s_reason ?? "No CUDA or OpenCL device.";

    private static string? s_reason;

    /// <summary>
    /// Gets the solver factory for <see cref="AirflowSimulation"/>: on the graphics card (the CPU's for a grid the card
    /// can't take), or <c>null</c> (the CPU solver) without one.
    /// </summary>
    public static Func<AirflowGrid, AirflowConditions, IAirflowSolver>? Factory => IsAvailable ? CreateOrFallBack : null;

    private static IAirflowSolver CreateOrFallBack(AirflowGrid grid, AirflowConditions conditions)
    {
        try
        {
            return CreateSolver(grid, conditions);
        }
        catch (Exception e) when (e is ILGPU.Runtime.AcceleratorException or OutOfMemoryException or InvalidOperationException)
        {
            return new AirflowSolver(grid, conditions);
        }
    }

    /// <summary>Creates a solver on the graphics card.</summary>
    /// <exception cref="InvalidOperationException">There is no graphics card.</exception>
    public static IAirflowSolver CreateSolver(AirflowGrid grid, AirflowConditions conditions) =>
        new GpuAirflowSolver(s_device.Value ?? throw new InvalidOperationException("There is no graphics card the airflow solver can use."), grid, conditions);

    /// <summary>Creates a solver running the kernels on ILGPU's CPU accelerator (slow; for tests without a card).</summary>
    internal static IAirflowSolver CreateCpuSolver(AirflowGrid grid, AirflowConditions conditions) =>
        new GpuAirflowSolver(s_cpu.Value ?? throw new InvalidOperationException("ILGPU's CPU accelerator is missing."), grid, conditions);

    private static GpuDevice? Open(bool cpu)
    {
        try
        {
            var device = GpuDevice.Create(cpu);
            if (device != null) AppDomain.CurrentDomain.ProcessExit += (_, _) => device.Dispose();
            return device;
        }
        catch (Exception e)
        {
            // No driver, or a card ILGPU can't use: the CPU solver it is.
            if (!cpu) s_reason = e.Message;
            else throw;
            return null;
        }
    }
}
