using ParagliderToolbox.Paraglider.Design;

namespace ParagliderToolbox.Paraglider.Proxy;

/// <summary>
/// The aerodynamic coefficients of a canopy section over the full range of angles of attack: attached flow with a lift
/// slope and zero-lift angle from the profile's camber and reflex, a stall break, and flat-plate coefficients for deep
/// stall and backward flow.
/// </summary>
/// <remarks>
/// Paraglider sections at Reynolds numbers around 10⁶ have a 2D lift slope near 2π·0.9, stall at 12–16°, and a profile
/// drag coefficient around 0.012–0.015 (the inlets, the ballooning and the seams add to the airfoil's own; calibrated with
/// the pilot and line drag to measured class polars). Beyond the stall
/// the coefficients blend into a flat plate's (Cl ≈ 1.1·sin 2α, Cd ≈ 1.9·sin²α), which is what deep stalls and
/// spins need.
/// </remarks>
public sealed class SectionPolar
{
    /// <summary>Initializes the polar of a design's profile.</summary>
    public SectionPolar(GliderDesign design)
    {
        ZeroLiftAlpha = -(design.Camber * 100 * 0.95) + design.Reflex * 100 * 1.2;
        StallAlpha = 12 + (design.RootThickness - 0.14) * 60;
        ProfileDrag = 0.009 + design.Ballooning * 0.04;
        MomentCoefficient = -(design.Camber * 2.2) + design.Reflex * 9;
    }

    /// <summary>Gets the 2D lift slope (per radian).</summary>
    public double LiftSlope { get; init; } = 2 * Math.PI * 0.9;

    /// <summary>Gets the zero-lift angle of attack (degrees).</summary>
    public double ZeroLiftAlpha { get; }

    /// <summary>Gets the stall angle of attack (degrees).</summary>
    public double StallAlpha { get; }

    /// <summary>Gets the negative stall angle (degrees).</summary>
    public double NegativeStallAlpha { get; init; } = -9;

    /// <summary>Gets the zero-lift drag coefficient.</summary>
    public double ProfileDrag { get; }

    /// <summary>Gets the drag rise with lift (Cd = Cd0 + k·Cl²).</summary>
    public double LiftDragFactor { get; init; } = 0.004;

    /// <summary>Gets the pitching moment coefficient about the quarter chord in attached flow (negative is nose down).</summary>
    public double MomentCoefficient { get; }

    /// <summary>Gets the lift, drag and moment coefficients at angle of attack <paramref name="alphaDegrees"/>.</summary>
    public (double Lift, double Drag, double Moment) Evaluate(double alphaDegrees)
    {
        double alpha = Normalize(alphaDegrees);
        double radians = alpha * Math.PI / 180;

        double attachedLift = LiftSlope * (alpha - ZeroLiftAlpha) * Math.PI / 180;
        double attachedDrag = ProfileDrag + LiftDragFactor * attachedLift * attachedLift;
        double plateLift = 1.1 * Math.Sin(2 * radians);
        double plateDrag = 0.05 + 1.9 * Math.Sin(radians) * Math.Sin(radians);
        double plateMoment = -0.45 * Math.Sin(radians);

        // 0 while attached, 1 once fully separated; the break is a few degrees wide.
        double separated = alpha >= 0
            ? Sigmoid((alpha - StallAlpha) / 2.5)
            : Sigmoid((NegativeStallAlpha - alpha) / 2.5);
        double lift = attachedLift * (1 - separated) + plateLift * separated;
        double drag = attachedDrag * (1 - separated) + Math.Max(plateDrag, attachedDrag) * separated;
        double moment = MomentCoefficient * (1 - separated) + plateMoment * separated;
        return (lift, drag, moment);
    }

    /// <summary>Samples the polar every degree from −180 to 180, for the proxy export.</summary>
    public ProxyPolar Sample()
    {
        var polar = new ProxyPolar();
        for (int a = -180; a <= 180; a++)
        {
            var (cl, cd, cm) = Evaluate(a);
            polar.AlphaDegrees.Add(a);
            polar.Lift.Add((float)cl);
            polar.Drag.Add((float)cd);
            polar.Moment.Add((float)cm);
        }
        return polar;
    }

    private static double Normalize(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees < -180) degrees += 360;
        return degrees;
    }

    private static double Sigmoid(double x) => 1 / (1 + Math.Exp(-x));
}
