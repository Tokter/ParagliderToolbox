using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;

namespace ParagliderToolbox.Airflow.Gpu;

/// <summary>
/// An ILGPU accelerator with the airflow kernels compiled for it, shared by the solvers on it (compiling them takes a
/// few seconds once). Solvers use it one at a time (<see cref="Gate"/>), bound to the calling thread.
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    private GpuDevice(Context context, Accelerator accelerator)
    {
        Context = context;
        Accelerator = accelerator;
        var a = accelerator;
        ExtrapolateX = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>>(GpuKernels.ExtrapolateX);
        ExtrapolateZ = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>>(GpuKernels.ExtrapolateZ);
        ExtrapolateCells = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ScalarViews>(GpuKernels.ExtrapolateCells);
        AdvectFaces = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, VelocityViews, Staggered, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, float>(GpuKernels.AdvectFaces);
        CorrectFaces = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, VelocityViews, Staggered, Staggered, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, float>(GpuKernels.CorrectFaces);
        AdvectCells = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, VelocityViews, ScalarViews, ScalarViews, ScalarViews, ScalarViews, float>(
            GpuKernels.AdvectCells);
        CorrectCells = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, VelocityViews, ScalarViews, ScalarViews, ScalarViews, ScalarViews, ScalarViews,
            float>(GpuKernels.CorrectCells);
        LevelSums = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ScalarViews, ArrayView<float>>(GpuKernels.LevelSums);
        LevelMeans = a.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>>(GpuKernels.LevelMeans);
        ApplyBuoyancy = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ScalarViews, ArrayView<float>, float>(GpuKernels.ApplyBuoyancy);
        Viscosity = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, VelocityViews, VelocityViews, ArrayView<float>, ArrayView<float>, float, float>(
            GpuKernels.Viscosity);
        DiffuseFaces = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int, int,
            ArrayView<float>, float, float, float>(GpuKernels.DiffuseFaces);
        DiffuseCells = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, float, float, float>(
            GpuKernels.DiffuseCells);
        Surface = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, SurfaceViews, VelocityViews, ScalarViews, ArrayView<float>, float>(GpuKernels.Surface);
        DragX = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, float>(GpuKernels.DragX);
        DragY = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, float>(GpuKernels.DragY);
        DragZ = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, float>(GpuKernels.DragZ);
        Condense = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ScalarViews, float>(GpuKernels.Condense);
        RelaxX = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams, float>(GpuKernels.RelaxX);
        RelaxY = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams, float>(GpuKernels.RelaxY);
        PerturbInflow = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, BoundaryParams, int, float, int>(GpuKernels.PerturbInflow);
        Shade = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, SurfaceViews, ArrayView<float>, float, float, float, float>(GpuKernels.Shade);
        RelaxZ = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams, float>(GpuKernels.RelaxZ);
        RelaxCells = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ScalarViews, ArrayView<float>, ArrayView<float>, BoundaryParams, float>(
            GpuKernels.RelaxCells);
        BoundaryX = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams>(GpuKernels.BoundaryX);
        BoundaryZ = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams>(GpuKernels.BoundaryZ);
        CloseFaces = a.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>>(GpuKernels.CloseFaces);
        Divergence = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, VelocityViews, ArrayView<float>, ArrayView<float>>(GpuKernels.Divergence);
        ProjectX = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams>(GpuKernels.ProjectX);
        ProjectY = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>>(GpuKernels.ProjectY);
        ProjectZ = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams>(GpuKernels.ProjectZ);
        Centers = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, VelocityViews, VelocityViews>(GpuKernels.Centers);
        Multiply = a.LoadAutoGroupedStreamKernel<Index1D, LevelViews, ArrayView<float>, ArrayView<float>>(GpuKernels.Multiply);
        Residual = a.LoadAutoGroupedStreamKernel<Index1D, LevelViews, ArrayView<float>, ArrayView<float>, ArrayView<float>>(GpuKernels.Residual);
        Smooth = a.LoadAutoGroupedStreamKernel<Index1D, LevelViews, ArrayView<float>, ArrayView<float>, int>(GpuKernels.Smooth);
        SolveCoarsest = a.LoadStreamKernel<LevelViews, ArrayView<float>, ArrayView<float>, int>(GpuKernels.SolveCoarsest);
        Restrict = a.LoadAutoGroupedStreamKernel<Index1D, LevelViews, LevelViews, ArrayView<float>, ArrayView<float>>(GpuKernels.Restrict);
        Prolong = a.LoadAutoGroupedStreamKernel<Index1D, LevelViews, LevelViews, ArrayView<float>, ArrayView<float>>(GpuKernels.Prolong);
        UpdateSolution = a.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, float>(
            GpuKernels.UpdateSolution);
        UpdateDirection = a.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, float>(GpuKernels.UpdateDirection);
        Dot = a.LoadStreamKernel<ArrayView<float>, ArrayView<float>, ArrayView<float>, int>(GpuKernels.Dot);
        MaxAbs = a.LoadStreamKernel<ArrayView<float>, ArrayView<int>, int>(GpuKernels.MaxAbs);
        Accumulate = a.LoadAutoGroupedStreamKernel<Index1D, GridViews, AverageViews, VelocityViews, ArrayView<float>, ScalarViews, float, float>(
            GpuKernels.Accumulate);
        // A grid-stride reduction fills the device: a few groups per multiprocessor.
        ReductionConfig = new KernelConfig(Math.Max(1, accelerator.NumMultiprocessors) * 4, Math.Min(256, accelerator.MaxNumThreadsPerGroup));
    }

    /// <summary>Gets the ILGPU context.</summary>
    public Context Context { get; }

    /// <summary>Gets the accelerator.</summary>
    public Accelerator Accelerator { get; }

    /// <summary>Gets the lock solvers hold while they use the device.</summary>
    public object Gate { get; } = new();

    /// <summary>Gets the device's name for people.</summary>
    public string Name => Accelerator.AcceleratorType == AcceleratorType.CPU ? "CPU (ILGPU)" : Accelerator.Name;

    /// <summary>Gets whether it is a graphics card (not ILGPU's CPU accelerator).</summary>
    public bool IsGpu => Accelerator.AcceleratorType != AcceleratorType.CPU;

    public KernelConfig ReductionConfig { get; }

    public Action<Index1D, GridViews, ArrayView<float>> ExtrapolateX { get; }
    public Action<Index1D, GridViews, ArrayView<float>> ExtrapolateZ { get; }
    public Action<Index1D, GridViews, ScalarViews> ExtrapolateCells { get; }
    public Action<Index1D, GridViews, VelocityViews, Staggered, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, float> AdvectFaces { get; }
    public Action<Index1D, GridViews, VelocityViews, Staggered, Staggered, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, float> CorrectFaces { get; }
    public Action<Index1D, GridViews, VelocityViews, ScalarViews, ScalarViews, ScalarViews, ScalarViews, float> AdvectCells { get; }
    public Action<Index1D, GridViews, VelocityViews, ScalarViews, ScalarViews, ScalarViews, ScalarViews, ScalarViews, float> CorrectCells { get; }
    public Action<Index1D, GridViews, ScalarViews, ArrayView<float>> LevelSums { get; }
    public Action<Index1D, ArrayView<float>, ArrayView<float>> LevelMeans { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ScalarViews, ArrayView<float>, float> ApplyBuoyancy { get; }
    public Action<Index1D, GridViews, VelocityViews, VelocityViews, ArrayView<float>, ArrayView<float>, float, float> Viscosity { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int, int, ArrayView<float>, float, float, float> DiffuseFaces { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, float, float, float> DiffuseCells { get; }
    public Action<Index1D, GridViews, SurfaceViews, VelocityViews, ScalarViews, ArrayView<float>, float> Surface { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, float> DragX { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, float> DragY { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, float> DragZ { get; }
    public Action<Index1D, GridViews, ScalarViews, float> Condense { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams, float> RelaxX { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams, float> RelaxY { get; }
    public Action<Index1D, GridViews, SurfaceViews, ArrayView<float>, float, float, float, float> Shade { get; }
    public Action<Index1D, GridViews, ArrayView<float>, BoundaryParams, int, float, int> PerturbInflow { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams, float> RelaxZ { get; }
    public Action<Index1D, GridViews, ScalarViews, ArrayView<float>, ArrayView<float>, BoundaryParams, float> RelaxCells { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams> BoundaryX { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams> BoundaryZ { get; }
    public Action<Index1D, ArrayView<float>, ArrayView<float>> CloseFaces { get; }
    public Action<Index1D, GridViews, VelocityViews, ArrayView<float>, ArrayView<float>> Divergence { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams> ProjectX { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>> ProjectY { get; }
    public Action<Index1D, GridViews, ArrayView<float>, ArrayView<float>, BoundaryParams> ProjectZ { get; }
    public Action<Index1D, GridViews, VelocityViews, VelocityViews> Centers { get; }
    public Action<Index1D, LevelViews, ArrayView<float>, ArrayView<float>> Multiply { get; }
    public Action<Index1D, LevelViews, ArrayView<float>, ArrayView<float>, ArrayView<float>> Residual { get; }
    public Action<Index1D, LevelViews, ArrayView<float>, ArrayView<float>, int> Smooth { get; }
    public Action<KernelConfig, LevelViews, ArrayView<float>, ArrayView<float>, int> SolveCoarsest { get; }
    public Action<Index1D, LevelViews, LevelViews, ArrayView<float>, ArrayView<float>> Restrict { get; }
    public Action<Index1D, LevelViews, LevelViews, ArrayView<float>, ArrayView<float>> Prolong { get; }
    public Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, float> UpdateSolution { get; }
    public Action<Index1D, ArrayView<float>, ArrayView<float>, float> UpdateDirection { get; }
    public Action<KernelConfig, ArrayView<float>, ArrayView<float>, ArrayView<float>, int> Dot { get; }
    public Action<KernelConfig, ArrayView<float>, ArrayView<int>, int> MaxAbs { get; }
    public Action<Index1D, GridViews, AverageViews, VelocityViews, ArrayView<float>, ScalarViews, float, float> Accumulate { get; }

    /// <summary>Creates the device: the first graphics card (CUDA, else OpenCL), or with <paramref name="cpu"/> ILGPU's CPU accelerator; <c>null</c> without one.</summary>
    public static GpuDevice? Create(bool cpu)
    {
        var context = Context.Create(builder => builder.Default().EnableAlgorithms().Math(MathMode.Fast32BitOnly).Optimize(OptimizationLevel.O2));
        try
        {
            var device = cpu ? context.GetCPUDevice(0) : context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda)
                ?? context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL);
            if (device is null)
            {
                context.Dispose();
                return null;
            }
            return new GpuDevice(context, device.CreateAccelerator(context));
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // After a running step (at the process' exit, a simulation may still be running).
        lock (Gate)
        {
            Accelerator.Dispose();
            Context.Dispose();
        }
    }
}
