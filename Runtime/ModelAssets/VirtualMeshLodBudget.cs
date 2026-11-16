using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.VirtualMesh.Runtime
{
    [Serializable]
    public sealed class VirtualMeshLodBudgetSettings
    {
        public bool Enabled = true;
        [Min(1)] public int NearTriangleBudget = 50000;
        [Min(1)] public int MidTriangleBudget = 5000;
        [Min(1)] public int FarTriangleBudget = 500;
        [Min(.0001f)] public float NearScreenHeight = .5f;
        [Min(.0001f)] public float MidScreenHeight = .15f;
        [Min(.0001f)] public float FarScreenHeight = .03f;
        [Min(.001f)] public float BudgetMultiplier = 1f;
    }

    /// <summary>Small per-model LOD table lookup before GPU cluster selection. Does not modify
    /// geometry, visibility, or a camera. The triangle estimate is a conservative requested-level
    /// bound, not a GPU measurement. Unsupported/partial profiles are reported honestly.</summary>
    public static class VirtualMeshLodBudget
    {
        [Serializable]
        public struct Selection
        {
            public float Scale;
            public int RequestedTriangles;
            public long EstimatedTriangles;
            public bool Unmet, Profiled;
            public bool EligibilityLimited, EligibilityValid;
        }

        internal sealed class Profile
        {
            public int Revision, Version, LeafVersion;
            public string Hash, LeafHash;
            public VirtualMeshModelLodLevel[] Source, LeafSource;
            public long[] Finest;
            public long TotalFinest;
            public VirtualMeshModelLodLevel[][] Rows;
            public float LodRadius;
            public bool HasEligibility, InvalidEligibility;
            public bool RequiresEligibility => HasEligibility || InvalidEligibility;
            public bool HasUniformEligibilitySphere;
            public Vector4 UniformEligibilitySphere;
        }
        struct LocalGroup
        {
            public bool HasFinest, HasTerminal, Invalid;
        }
        static readonly Dictionary<VirtualMeshModelAsset, Profile> profiles = new Dictionary<VirtualMeshModelAsset, Profile>();

        internal readonly struct BudgetCurve
        {
            private readonly struct Segment
            {
                private readonly double lowerHeight, logHeightSpan, logLowerBudget, logBudgetSpan;

                public Segment(double low, double high, double lowerBudget, double upperBudget)
                {
                    lowerHeight = low;
                    logHeightSpan = Math.Log(high / low);
                    logLowerBudget = Math.Log(lowerBudget);
                    logBudgetSpan = Math.Log(upperBudget) - logLowerBudget;
                }

                public double Interpolate(double height)
                {
                    double t = Math.Log(height / lowerHeight) / logHeightSpan;
                    t = t * t * (3 - 2 * t);
                    return Math.Exp(logLowerBudget + logBudgetSpan * t);
                }
            }

            private readonly double far, near, low, middle, high, multiplier;
            private readonly Segment farToMid, midToNear;
            public readonly bool Enabled;

            public BudgetCurve(VirtualMeshLodBudgetSettings settings)
            {
                this = default;
                if (settings == null || !settings.Enabled) return;
                Enabled = true;
                far = Math.Max(1, settings.FarTriangleBudget);
                double mid = Math.Max(far, settings.MidTriangleBudget);
                near = Math.Max(mid, settings.NearTriangleBudget);
                low = settings.FarScreenHeight;
                middle = settings.MidScreenHeight;
                high = settings.NearScreenHeight;
                if (!Finite(low) || !Finite(middle) || !Finite(high) || low <= 0 || middle <= low || high <= middle)
                { low = .03; middle = .15; high = .5; }
                farToMid = new Segment(low, middle, far, mid);
                midToNear = new Segment(middle, high, mid, near);
                multiplier = Finite(settings.BudgetMultiplier) ? Math.Max(.001, settings.BudgetMultiplier) : 1;
            }

            public int Evaluate(float screenHeight)
            {
                if (!Enabled) return int.MaxValue;
                double height = Finite(screenHeight) ? Math.Max(0, screenHeight) : high;
                double budget = height >= high ? near : height <= low ? far :
                    height >= middle ? midToNear.Interpolate(height) : farToMid.Interpolate(height);
                return (int)Math.Max(1, Math.Min(int.MaxValue, Math.Round(budget * multiplier)));
            }
        }

        public static int EvaluateBudget(VirtualMeshLodBudgetSettings settings, float screenHeight) =>
            new BudgetCurve(settings).Evaluate(screenHeight);

        /// <summary>Multiply both pixelError and nonzero targetPixels by the returned Scale.
        /// Every GPU interval then scales uniformly. unitsPerPixel must be a lower bound for
        /// every local group, using GetConservativeLodRadius for perspective projection.
        /// The simultaneous-capacity bound must continue to use the ORIGINAL parameters.</summary>
        public static Selection Evaluate(VirtualMeshModelAsset model, int requestedBudget,
            float unitsPerPixel, float pixelError, float targetPixels)
        {
            if (model == null || !model.IsValid)
                return new Selection { Scale = 1, RequestedTriangles = Math.Max(1, requestedBudget), EligibilityValid = true };
            var profile = GetProfile(model);
            if (profile.RequiresEligibility)
                return new Selection { Scale = 1, RequestedTriangles = Math.Max(1, requestedBudget),
                    EstimatedTriangles = profile.TotalFinest, Profiled = true, Unmet = true, EligibilityValid = false };
            return EvaluateProfile(profile, requestedBudget, unitsPerPixel, pixelError, targetPixels);
        }

        /// <summary>Applies coverage eligibility even when triangle budgeting is disabled.
        /// The minimum/maximum projection units must bound every GPU LOD-group unit.
        /// projectedModelPixels is the conservative shared model-sphere diameter.</summary>
        public static Selection EvaluateWithEligibility(VirtualMeshModelAsset model, int requestedBudget,
            float minimumUnitsPerPixel, float maximumUnitsPerPixel, float projectedModelPixels,
            float pixelError, float targetPixels, bool budgetEnabled)
        {
            if (model == null || !model.IsValid)
                return new Selection { Scale = 1, RequestedTriangles = Math.Max(1, requestedBudget), EligibilityValid = true };
            return EvaluateEligibleProfile(GetProfile(model), requestedBudget, minimumUnitsPerPixel, maximumUnitsPerPixel,
                projectedModelPixels, pixelError, targetPixels, budgetEnabled);
        }

        internal static Selection EvaluateEligibleProfile(Profile profile, int requestedBudget, float minimumUnitsPerPixel,
            float maximumUnitsPerPixel, float projectedModelPixels, float pixelError, float targetPixels, bool budgetEnabled)
        {
            var result = budgetEnabled ? EvaluateProfile(profile, requestedBudget, minimumUnitsPerPixel, pixelError, targetPixels) :
                new Selection { Scale = 1, RequestedTriangles = Math.Max(1, requestedBudget), EstimatedTriangles = profile.TotalFinest,
                    Profiled = true, EligibilityValid = true };
            if (profile.InvalidEligibility) {
                result.Scale = 1; result.EstimatedTriangles = profile.TotalFinest; result.Unmet = true;
                result.EligibilityValid = false; return result;
            }
            if (!profile.HasEligibility) return result;
            if (!Finite(minimumUnitsPerPixel) || minimumUnitsPerPixel <= 0 || !Finite(maximumUnitsPerPixel) ||
                maximumUnitsPerPixel < minimumUnitsPerPixel || !Finite(projectedModelPixels) || projectedModelPixels < 0 ||
                !Finite(pixelError) || pixelError < .000001f || !Finite(targetPixels) || targetPixels < 0)
            {
                result.Scale = 1; result.EstimatedTriangles = profile.TotalFinest; result.Unmet = true;
                result.EligibilityValid = false; return result;
            }
            double cap = double.PositiveInfinity;
            foreach (var rows in profile.Rows)
                foreach (var row in rows)
                    if (row.MaxProjectedPixels > 0 && projectedModelPixels > row.MaxProjectedPixels)
                    {
                        float transition = VirtualMeshModelCluster.TransitionThreshold(row.Error, row.TriangleSpacing, pixelError, targetPixels);
                        // Strictly below the half-open entry threshold, even after float
                        // division/multiplication. Cap uses the FARTHEST group's units.
                        cap = Math.Min(cap, (double)transition / maximumUnitsPerPixel * .99999);
                    }
            // Avoid a subnormal increment loop on a process using flush-to-zero.
            float minimumScale = Math.Max(1.17549435e-38f, (float)(.000001 / pixelError));
            while (pixelError * minimumScale < .000001f)
                minimumScale = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(minimumScale) + 1);
            if (cap < minimumScale || double.IsNaN(cap))
            {
                // The shader's pixel-error clamp would break uniform scaling here.
                // Caller must refuse this malformed/unusable new representation.
                result.Scale = 1; result.EstimatedTriangles = profile.TotalFinest; result.Unmet = true;
                result.EligibilityValid = false; return result;
            }
            if (cap < result.Scale)
            {
                float rounded = (float)cap;
                if (rounded > cap) rounded = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(rounded) - 1);
                result.Scale = Math.Max(minimumScale, rounded); result.EligibilityLimited = true;
            }
            result.EstimatedTriangles = 0;
            bool incomplete = false;
            for (int slot = 0; slot < profile.Finest.Length; slot++)
            {
                long bound = profile.Finest[slot];
                var rows = profile.Rows[slot];
                if (rows.Length == 0 && bound != 0) incomplete = true;
                foreach (var row in rows)
                {
                    if (row.MaxProjectedPixels > 0 && projectedModelPixels > row.MaxProjectedPixels) continue;
                    // Re-evaluate with the ACTUAL scaled float arguments used by HLSL,
                    // rather than trusting the estimate before the safety cap.
                    float threshold = VirtualMeshModelCluster.TransitionThreshold(row.Error, row.TriangleSpacing,
                        pixelError * result.Scale, targetPixels * result.Scale);
                    if (threshold <= minimumUnitsPerPixel) bound = Math.Min(bound, row.TriangleCount);
                }
                result.EstimatedTriangles += bound;
            }
            result.Unmet = budgetEnabled && (incomplete || result.EstimatedTriangles > result.RequestedTriangles);
            return result;
        }

        static Selection EvaluateProfile(Profile profile, int requestedBudget, float unitsPerPixel, float pixelError, float targetPixels)
        {
            var result = new Selection { Scale = 1, RequestedTriangles = Math.Max(1, requestedBudget), Profiled = true, EligibilityValid = true };
            if (profile.TotalFinest <= 0) { result.Unmet = true; return result; }
            if (!Finite(unitsPerPixel) || unitsPerPixel <= 0 || !Finite(pixelError) || pixelError < .000001f ||
                !Finite(targetPixels) || targetPixels < 0)
            { result.EstimatedTriangles = profile.TotalFinest; result.Unmet = true; return result; }
            double requiredScale = 1;
            bool uncertainEstimate = false;
            for (int submesh = 0; submesh < profile.Finest.Length; submesh++)
            {
                long finest = profile.Finest[submesh]; if (finest == 0) continue;
                long quota = Math.Max(1, (long)Math.Floor((double)result.RequestedTriangles * finest / profile.TotalFinest));
                if (finest <= quota) { result.EstimatedTriangles += finest; continue; }
                var rows = profile.Rows[submesh];
                if (rows == null || rows.Length == 0)
                {
                    // A malformed/incomplete old hierarchy is never treated as a
                    // successful coarse level merely because a sibling slot has one.
                    result.EstimatedTriangles += finest; result.Unmet = true; continue;
                }
                var chosen = rows[rows.Length - 1];
                foreach (var row in rows) if (row.TriangleCount <= quota) { chosen = row; break; }
                result.EstimatedTriangles += chosen.TriangleCount;
                result.Unmet |= chosen.TriangleCount > quota;
                float transition = VirtualMeshModelCluster.TransitionThreshold(chosen.Error, chosen.TriangleSpacing, pixelError, targetPixels);
                if (!Finite(transition)) { result.Unmet = true; uncertainEstimate = true; continue; }
                requiredScale = Math.Max(requiredScale, (double)transition / unitsPerPixel);
            }
            // A small upward margin covers CPU/GPU multiply/divide rounding at a half-open edge.
            // Never upload infinity even for invalid/tiny camera projection scales.
            double maxScale = Math.Min(1e20, float.MaxValue / (double)Math.Max(pixelError, Math.Max(1, targetPixels)) * .5);
            if (requiredScale > 1) requiredScale *= 1.00001;
            if (!Finite(requiredScale) || requiredScale > maxScale)
            { requiredScale = maxScale; result.Unmet = true; uncertainEstimate = true; }
            result.Scale = (float)Math.Max(1, requiredScale);
            if (uncertainEstimate) result.EstimatedTriangles = profile.TotalFinest;
            result.Unmet |= result.EstimatedTriangles > result.RequestedTriangles;
            return result;
        }

        internal static Profile GetProfile(VirtualMeshModelAsset model)
        {
            if (profiles.TryGetValue(model, out var cached) && cached.Revision == model.Revision &&
                cached.Version == model.CoarseLodVersion && cached.Hash == model.CoarseLodSettingsHash &&
                cached.LeafVersion == model.LeafLodVersion && cached.LeafHash == model.LeafLodSettingsHash &&
                ReferenceEquals(cached.Source, model.CoarseLodLevels) && ReferenceEquals(cached.LeafSource, model.LeafLodLevels)) return cached;
            var profile = new Profile { Revision = model.Revision, Version = model.CoarseLodVersion,
                Hash = model.CoarseLodSettingsHash, Source = model.CoarseLodLevels,
                LeafVersion = model.LeafLodVersion, LeafHash = model.LeafLodSettingsHash, LeafSource = model.LeafLodLevels,
                Finest = new long[model.SubMeshCount], Rows = new VirtualMeshModelLodLevel[model.SubMeshCount][] };
            var groups = new Dictionary<Vector4, LocalGroup>[model.SubMeshCount];
            var roots = new long[model.SubMeshCount];
            var checkpointError = new float[model.SubMeshCount];
            var checkpointSpacing = new float[model.SubMeshCount];
            var invalidSlots = new bool[model.SubMeshCount];
            profile.LodRadius = model.Bounds.extents.magnitude;
            for (int slot = 0; slot < groups.Length; slot++) groups[slot] = new Dictionary<Vector4, LocalGroup>();
            // Once per geometry revision, never once per instance or frame.
            foreach (var cluster in model.Clusters)
            {
                int slot = (int)cluster.submesh;
                if (slot < 0 || slot >= groups.Length) continue; // IsValid normally rejects this.
                if (cluster.type == 0 || cluster.type == 1) profile.Finest[slot] += cluster.indexCount / 3;
                if (cluster.type == 0 || cluster.type == 3) roots[slot] += cluster.indexCount / 3;
                var sphere = new Vector4(cluster.center.x, cluster.center.y, cluster.center.z, cluster.radius);
                groups[slot].TryGetValue(sphere, out var group);
                group.HasFinest |= cluster.type == 0 || cluster.type == 1;
                group.HasTerminal |= cluster.type == 0 || cluster.type == 3;
                bool finiteSphere = Finite(sphere.x) && Finite(sphere.y) && Finite(sphere.z) && Finite(sphere.w) && sphere.w >= 0;
                group.Invalid |= !finiteSphere || cluster.type > 3 || cluster.indexCount == 0 || cluster.indexCount % 3 != 0 ||
                    !Finite(cluster.error) || cluster.error < 0 || !Finite(cluster.parentError) || cluster.parentError < 0 ||
                    !Finite(cluster.triangleSpacing) || cluster.triangleSpacing < 0 ||
                    !Finite(cluster.parentTriangleSpacing) || cluster.parentTriangleSpacing < 0 ||
                    (cluster.type == 2 && (cluster.parentError < cluster.error || cluster.parentTriangleSpacing < cluster.triangleSpacing));
                groups[slot][sphere] = group;
                invalidSlots[slot] |= group.Invalid;
                // Roots must turn on AND every non-root interval must end.
                // Transition(error,spacing) is monotone in both arguments.
                if (cluster.type == 3) {
                    checkpointError[slot] = Math.Max(checkpointError[slot], cluster.error);
                    checkpointSpacing[slot] = Math.Max(checkpointSpacing[slot], cluster.triangleSpacing);
                } else if (cluster.type == 1 || cluster.type == 2) {
                    checkpointError[slot] = Math.Max(checkpointError[slot], cluster.parentError);
                    checkpointSpacing[slot] = Math.Max(checkpointSpacing[slot], cluster.parentTriangleSpacing);
                }
                if (finiteSphere) {
                    // A local box's enclosing sphere can protrude beyond the model's
                    // enclosing sphere even when all original vertices are inside it.
                    double dx = (double)cluster.center.x - model.Bounds.center.x;
                    double dy = (double)cluster.center.y - model.Bounds.center.y;
                    double dz = (double)cluster.center.z - model.Bounds.center.z;
                    double radius = Math.Sqrt(dx * dx + dy * dy + dz * dz) + cluster.radius;
                    double enlarged = Math.Min(float.MaxValue, Math.Max(profile.LodRadius, radius));
                    float rounded = (float)enlarged;
                    if (rounded < enlarged) rounded = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(rounded) + 1);
                    profile.LodRadius = rounded;
                }
            }
            for (int slot = 0; slot < model.SubMeshCount; slot++)
            {
                var rows = new List<VirtualMeshModelLodLevel>(); bool hasGlobal = false;
                bool leafSlot = false;
                foreach (var row in model.LeafLodLevels ?? Array.Empty<VirtualMeshModelLodLevel>())
                    if (row.Submesh == slot) { rows.Add(row); leafSlot = true; hasGlobal |= row.Level > 0 && row.FirstCluster >= 0; }
                if (!leafSlot) foreach (var row in model.CoarseLodLevels ?? Array.Empty<VirtualMeshModelLodLevel>())
                    if (row.Submesh == slot) { rows.Add(row); hasGlobal |= row.Level > 0 && row.FirstCluster >= 0; }
                rows.Sort((a, b) => a.Level.CompareTo(b.Level));
                if (profile.Finest[slot] == 0 && rows.Count > 0) profile.Finest[slot] = rows[0].TriangleCount;
                profile.TotalFinest += profile.Finest[slot];
                // Level zero is a conservative checkpoint only after a real global chain was added:
                // its max root error/spacing and common model sphere reach all old roots or coarser.
                // A no-op profile may retain different local spheres, so do not force its aggregate.
                bool valid = (leafSlot ? model.LeafLodVersion : model.CoarseLodVersion) > 0 && hasGlobal && rows.Count > 1;
                float previousError = 0, previousSpacing = 0, previousPixelLimit = float.PositiveInfinity; int previousTriangles = int.MaxValue;
                foreach (var row in rows)
                {
                    float pixelLimit = row.MaxProjectedPixels > 0 ? row.MaxProjectedPixels : float.PositiveInfinity;
                    valid &= row.TriangleCount > 0 && row.TriangleCount <= previousTriangles &&
                        Finite(row.Error) && row.Error >= previousError && Finite(row.TriangleSpacing) && row.TriangleSpacing >= previousSpacing &&
                        Finite(row.MaxProjectedPixels) && row.MaxProjectedPixels >= 0 && pixelLimit <= previousPixelLimit &&
                        (row.MaxProjectedPixels == 0 || row.Error > 0);
                    previousError = row.Error; previousSpacing = row.TriangleSpacing; previousTriangles = row.TriangleCount;
                    previousPixelLimit = pixelLimit;
                }
                if (valid) {
                    profile.Rows[slot] = rows.ToArray();
                    foreach (var row in rows) profile.HasEligibility |= row.MaxProjectedPixels > 0;
                }
                else
                {
                    foreach (var row in rows) profile.InvalidEligibility |= row.MaxProjectedPixels != 0;
                    bool complete = !invalidSlots[slot] && groups[slot].Count > 0 && roots[slot] > 0 &&
                        roots[slot] <= profile.Finest[slot] && roots[slot] <= int.MaxValue;
                    foreach (var group in groups[slot].Values) complete &= group.HasFinest && group.HasTerminal && !group.Invalid;
                    // Existing alpha foliage groups already have useful local LODs.
                    // Their coarsest surface is a real, conservative checkpoint;
                    // it does not imply an unsupported whole-model simplification.
                    profile.Rows[slot] = complete ? new[] { new VirtualMeshModelLodLevel {
                        Submesh = slot, Level = 0, FirstCluster = -1, TriangleCount = (int)roots[slot],
                        Error = checkpointError[slot], TriangleSpacing = checkpointSpacing[slot] } } :
                        Array.Empty<VirtualMeshModelLodLevel>();
                }
            }
            if (profile.HasEligibility && !profile.InvalidEligibility)
            {
                var gatedSlots = new bool[model.SubMeshCount];
                for (int slot = 0; slot < gatedSlots.Length; slot++)
                    foreach (var row in profile.Rows[slot]) gatedSlots[slot] |= row.MaxProjectedPixels > 0;
                profile.HasUniformEligibilitySphere = FindUniformEligibilitySphere(model.Clusters, gatedSlots,
                    out profile.UniformEligibilitySphere);
            }
            profiles[model] = profile; return profile;
        }

        // Check every old and new cluster in every gated slot. Checking just the
        // appended rows would wrongly certify old local-group LOD spheres.
        static bool FindUniformEligibilitySphere(VirtualMeshModelCluster[] clusters, bool[] gatedSlots, out Vector4 sphere)
        {
            sphere = default;
            if (clusters == null || gatedSlots == null || gatedSlots.Length == 0) return false;
            int remaining = 0; foreach (bool gated in gatedSlots) if (gated) remaining++;
            if (remaining == 0) return false;
            var seen = new bool[gatedSlots.Length]; bool found = false;
            foreach (var cluster in clusters)
            {
                if (cluster.submesh >= gatedSlots.Length) return false;
                int slot = (int)cluster.submesh; if (!gatedSlots[slot]) continue;
                var candidate = new Vector4(cluster.center.x, cluster.center.y, cluster.center.z, cluster.radius);
                if (!Finite(candidate.x) || !Finite(candidate.y) || !Finite(candidate.z) || !Finite(candidate.w) || candidate.w < 0 ||
                    cluster.type > 3 || cluster.indexCount == 0 || cluster.indexCount % 3 != 0) return false;
                if (!found) { sphere = candidate; found = true; }
                else if (!SameBits(sphere.x, candidate.x) || !SameBits(sphere.y, candidate.y) ||
                    !SameBits(sphere.z, candidate.z) || !SameBits(sphere.w, candidate.w)) return false;
                if (!seen[slot]) { seen[slot] = true; remaining--; }
            }
            return found && remaining == 0;
        }

        static bool SameBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

        /// <summary>A cached proof that every cluster in every coverage-gated slot
        /// uses one bit-identical LOD sphere. Nongated slots may keep local spheres.</summary>
        public static bool TryGetUniformEligibilitySphere(VirtualMeshModelAsset model, out Vector4 sphere)
        {
            sphere = default;
            if (model == null || !model.IsValid) return false;
            var profile = GetProfile(model);
            if (!profile.HasUniformEligibilitySphere) return false;
            sphere = profile.UniformEligibilitySphere; return true;
        }

        /// <summary>Upper bound for the shader LOD units of a proven uniform sphere.
        /// maximumStretch must be the exact float uploaded in instance.parameters.x.
        /// Wind padding is intentionally omitted: nonnegative padding only decreases
        /// distance-to-surface. This bound is for coverage caps, not triangle budgeting.</summary>
        public static bool TryUniformEligibilityUpperUnits(Vector4 sphere, Matrix4x4 objectToWorld,
            float maximumStretch, Vector3 cameraPosition, Vector3 cameraForward, float pixelScale,
            float near, bool orthographic, bool shadow, out float units)
        {
            units = 0;
            if (!ProjectionFloat(sphere.x) || !ProjectionFloat(sphere.y) || !ProjectionFloat(sphere.z) || !ProjectionFloat(sphere.w) || sphere.w < 0 ||
                !ProjectionFloat(maximumStretch) || maximumStretch <= 0 || !ProjectionFloat(pixelScale) || pixelScale <= 0 ||
                !ProjectionFloat(near) || near <= 0 || !ProjectionFloat(cameraPosition.x) || !ProjectionFloat(cameraPosition.y) || !ProjectionFloat(cameraPosition.z) ||
                !ProjectionFloat(cameraForward.x) || !ProjectionFloat(cameraForward.y) || !ProjectionFloat(cameraForward.z)) return false;
            for (int i = 0; i < 16; i++) if (!ProjectionFloat(objectToWorld[i])) return false;
            if (objectToWorld.m30 != 0 || objectToWorld.m31 != 0 || objectToWorld.m32 != 0 || objectToWorld.m33 != 1) return false;
            double denominator = Math.Max((double).000001f, (double)pixelScale * maximumStretch);
            if (denominator > float.MaxValue) return false;
            double numerator = 1;
            if (!orthographic)
            {
                // Double precision avoids manufacturing a small error after large
                // float matrix terms cancel. The margin uses their absolute sums,
                // not merely the final (possibly tiny) transformed center.
                double x = (double)objectToWorld.m00 * sphere.x + (double)objectToWorld.m01 * sphere.y +
                    (double)objectToWorld.m02 * sphere.z + objectToWorld.m03;
                double y = (double)objectToWorld.m10 * sphere.x + (double)objectToWorld.m11 * sphere.y +
                    (double)objectToWorld.m12 * sphere.z + objectToWorld.m13;
                double z = (double)objectToWorld.m20 * sphere.x + (double)objectToWorld.m21 * sphere.y +
                    (double)objectToWorld.m22 * sphere.z + objectToWorld.m23;
                double ax = Math.Abs((double)objectToWorld.m00 * sphere.x) + Math.Abs((double)objectToWorld.m01 * sphere.y) +
                    Math.Abs((double)objectToWorld.m02 * sphere.z) + Math.Abs(objectToWorld.m03) + Math.Abs(cameraPosition.x);
                double ay = Math.Abs((double)objectToWorld.m10 * sphere.x) + Math.Abs((double)objectToWorld.m11 * sphere.y) +
                    Math.Abs((double)objectToWorld.m12 * sphere.z) + Math.Abs(objectToWorld.m13) + Math.Abs(cameraPosition.y);
                double az = Math.Abs((double)objectToWorld.m20 * sphere.x) + Math.Abs((double)objectToWorld.m21 * sphere.y) +
                    Math.Abs((double)objectToWorld.m22 * sphere.z) + Math.Abs(objectToWorld.m23) + Math.Abs(cameraPosition.z);
                double radius = (double)sphere.w * maximumStretch;
                // Refuse cases where the shader may overflow an intermediate.
                if (Math.Max(ax, Math.Max(ay, az)) > float.MaxValue * .25 || radius > float.MaxValue * .25) return false;
                x -= cameraPosition.x; y -= cameraPosition.y; z -= cameraPosition.z;
                double forwardWeight = Math.Max(1, Math.Abs(cameraForward.x) + (double)Math.Abs(cameraForward.y) + Math.Abs(cameraForward.z));
                double distance;
                if (shadow)
                {
                    double squared = x * x + y * y + z * z;
                    distance = Math.Sqrt(squared);
                    double lengthEnvelope = distance + 2e-6 * (ax + ay + az);
                    if (lengthEnvelope * lengthEnvelope > float.MaxValue * .25) return false;
                    forwardWeight = 1;
                }
                else
                {
                    double dotMagnitude = ax * Math.Abs(cameraForward.x) + ay * Math.Abs(cameraForward.y) + az * Math.Abs(cameraForward.z);
                    if (dotMagnitude > float.MaxValue * .25) return false;
                    distance = x * cameraForward.x + y * cameraForward.y + z * cameraForward.z;
                }
                double margin = 2e-6 * Math.Max(1, (ax + ay + az + radius + near) * forwardWeight);
                numerator = Math.Max(near, distance - radius + margin);
            }
            // Covers the uploaded float product/clamp, division, and shader FMA
            // differences. Round upward as well, never to the nearest smaller float.
            double upper = numerator / denominator * 1.000004;
            if (!Finite(upper) || upper <= 0 || upper > float.MaxValue) return false;
            units = (float)upper;
            if (units < upper) units = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(units) + 1);
            return Finite(units) && units > 0;
        }

        static bool ProjectionFloat(float value)
        {
            // GPU denormals may be flushed BEFORE multiplication. A subnormal
            // matrix term times a huge local coordinate can therefore lose a
            // non-small contribution; an ordinary roundoff margin cannot bound it.
            // Inspect bits so the CPU's own DAZ/FTZ mode cannot hide this case.
            uint magnitude = unchecked((uint)BitConverter.SingleToInt32Bits(value)) & 0x7fffffffu;
            return magnitude == 0 || magnitude >= 0x00800000u && magnitude < 0x7f800000u;
        }

        /// <summary>Model-local sphere radius enclosing every shared LOD group sphere.
        /// Use this radius, transformed conservatively and with wind padding, only
        /// for the projected-distance lower bound passed to Evaluate.</summary>
        public static float GetConservativeLodRadius(VirtualMeshModelAsset model) =>
            model != null && model.IsValid ? GetProfile(model).LodRadius : 0;

        public static bool RequiresEligibility(VirtualMeshModelAsset model)
        {
            if (model == null || !model.IsValid) return false;
            var profile = GetProfile(model);
            return profile.RequiresEligibility;
        }

        internal static void ClearCache() => profiles.Clear();
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
