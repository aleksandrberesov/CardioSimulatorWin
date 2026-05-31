namespace CardioSimulator.Core.Domain;

/// <summary>
/// Linear combinations and angular projections used to derive the missing
/// leads of a 12-lead ECG from a smaller recorded set using Einthoven /
/// Goldberger and V-lead angular projection.
///
/// Inputs and outputs are in source-coordinate units. The caller is
/// responsible for ensuring inputs are aligned in time and sampled at the
/// same rate; mismatches are silently truncated to the shorter length.
/// </summary>
public static class DerivedLeads
{
    /// <summary>
    /// Einthoven / Goldberger combinations from limb leads I and II.
    /// <code>
    ///  III  = II - I
    ///  aVR  = -(I + II) / 2
    ///  aVL  = (2·I - II) / 2
    ///  aVF  = (2·II - I) / 2
    /// </code>
    /// Returns an empty list if <paramref name="target"/> is not one of
    /// III/aVR/aVL/aVF or if either input is empty.
    /// </summary>
    public static IReadOnlyList<float> CombineIII_aVR_aVL_aVF(
        IReadOnlyList<float> leadI, IReadOnlyList<float> leadII, Lead target)
    {
        if (leadI.Count == 0 || leadII.Count == 0) return Array.Empty<float>();
        var n = Math.Min(leadI.Count, leadII.Count);
        var outArr = new float[n];
        switch (target)
        {
            case Lead.III:
                for (var i = 0; i < n; i++) outArr[i] = leadII[i] - leadI[i];
                break;
            case Lead.aVR:
                for (var i = 0; i < n; i++) outArr[i] = -(leadI[i] + leadII[i]) / 2f;
                break;
            case Lead.aVL:
                for (var i = 0; i < n; i++) outArr[i] = (2f * leadI[i] - leadII[i]) / 2f;
                break;
            case Lead.aVF:
                for (var i = 0; i < n; i++) outArr[i] = (2f * leadII[i] - leadI[i]) / 2f;
                break;
            default:
                return Array.Empty<float>();
        }
        return outArr;
    }

    /// <summary>
    /// Angular projection from V2 and V6 onto the missing V-lead positions.
    ///
    /// The precordial leads are assumed to be at fixed angles on the chest:
    /// V1: 115°, V2: 94°, V3: 70°, V4: 45°, V5: 23°, V6: 0°.
    ///
    /// Given V2 and V6 as basis vectors, each other lead's projection is
    /// derived by decomposing the angular position into cos/sin components
    /// relative to V2 and V6.
    /// </summary>
    public static IReadOnlyList<float> CombineV1_V3_V4_V5(
        IReadOnlyList<float> leadV2, IReadOnlyList<float> leadV6, Lead target)
    {
        if (leadV2.Count == 0 || leadV6.Count == 0) return Array.Empty<float>();
        var n = Math.Min(leadV2.Count, leadV6.Count);

        double? angleDeg = target switch
        {
            Lead.V1 => 115.0,
            Lead.V3 => 70.0,
            Lead.V4 => 45.0,
            Lead.V5 => 23.0,
            _ => null,
        };
        if (angleDeg is null) return Array.Empty<float>();

        var a = ToRadians(angleDeg.Value);
        var v2a = ToRadians(94.0);
        var v6a = ToRadians(0.0);

        // Decompose `a` into a 2D basis spanned by V2 (94°) and V6 (0°).
        //   cos(a) = α·cos(v2a) + β·cos(v6a)
        //   sin(a) = α·sin(v2a) + β·sin(v6a)
        var det = Math.Cos(v2a) * Math.Sin(v6a) - Math.Cos(v6a) * Math.Sin(v2a);
        if (det == 0.0) return Array.Empty<float>();
        var alpha = (Math.Cos(a) * Math.Sin(v6a) - Math.Cos(v6a) * Math.Sin(a)) / det;
        var beta = (Math.Cos(v2a) * Math.Sin(a) - Math.Cos(a) * Math.Sin(v2a)) / det;

        var outArr = new float[n];
        for (var i = 0; i < n; i++)
        {
            outArr[i] = (float)(alpha * leadV2[i] + beta * leadV6[i]);
        }
        return outArr;
    }

    /// <summary>
    /// Int-array wrapper over <see cref="CombineIII_aVR_aVL_aVF"/> that applies
    /// baseline subtraction before calculation and baseline addition after.
    /// </summary>
    public static int[] CombineIII_aVR_aVL_aVF(
        IReadOnlyList<int> leadI, IReadOnlyList<int> leadII, Lead target, int baseline)
    {
        var f1 = leadI.Select(x => (float)(x - baseline)).ToList();
        var f2 = leadII.Select(x => (float)(x - baseline)).ToList();
        var fOut = CombineIII_aVR_aVL_aVF(f1, f2, target);
        var intOut = new int[fOut.Count];
        for (var i = 0; i < fOut.Count; i++)
        {
            intOut[i] = (int)Math.Round(fOut[i]) + baseline;
        }
        return intOut;
    }

    /// <summary>
    /// Int-array wrapper over <see cref="CombineV1_V3_V4_V5"/> that applies
    /// baseline subtraction before calculation and baseline addition after.
    /// </summary>
    public static int[] CombineV1_V3_V4_V5(
        IReadOnlyList<int> leadV2, IReadOnlyList<int> leadV6, Lead target, int baseline)
    {
        var f2 = leadV2.Select(x => (float)(x - baseline)).ToList();
        var f6 = leadV6.Select(x => (float)(x - baseline)).ToList();
        var fOut = CombineV1_V3_V4_V5(f2, f6, target);
        var intOut = new int[fOut.Count];
        for (var i = 0; i < fOut.Count; i++)
        {
            intOut[i] = (int)Math.Round(fOut[i]) + baseline;
        }
        return intOut;
    }

    /// <summary>Set of leads producible from I + II.</summary>
    public static readonly IReadOnlySet<Lead> DerivableFromIandII =
        new HashSet<Lead> { Lead.III, Lead.aVR, Lead.aVL, Lead.aVF };

    /// <summary>Set of leads producible from V2 + V6.</summary>
    public static readonly IReadOnlySet<Lead> DerivableFromV2andV6 =
        new HashSet<Lead> { Lead.V1, Lead.V3, Lead.V4, Lead.V5 };

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
