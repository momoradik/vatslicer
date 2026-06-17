using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Checks for bounding box collisions between multiple models arranged on
/// the build plate. Reports overlapping pairs with intersection volume.
/// </summary>
public static class ModelCollisionChecker
{
    public sealed record ModelBounds
    {
        public required string Id { get; init; }
        public required Vector3 Min { get; init; }
        public required Vector3 Max { get; init; }
    }

    public sealed record Collision
    {
        public required string IdA { get; init; }
        public required string IdB { get; init; }
        public required float OverlapVolumeMm3 { get; init; }
    }

    public sealed record CollisionResult
    {
        public required List<Collision> Collisions { get; init; }
        public required bool HasCollisions { get; init; }
    }

    public static CollisionResult Check(IReadOnlyList<ModelBounds> models, float gapMm = 0)
    {
        var collisions = new List<Collision>();

        for (int i = 0; i < models.Count; i++)
        for (int j = i + 1; j < models.Count; j++)
        {
            var a = models[i]; var b = models[j];
            float ox = Math.Max(0, Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X) + gapMm);
            float oy = Math.Max(0, Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y) + gapMm);
            float oz = Math.Max(0, Math.Min(a.Max.Z, b.Max.Z) - Math.Max(a.Min.Z, b.Min.Z));

            if (ox > 0 && oy > 0 && oz > 0)
            {
                collisions.Add(new Collision
                {
                    IdA = a.Id, IdB = b.Id, OverlapVolumeMm3 = ox * oy * oz,
                });
            }
        }

        return new CollisionResult { Collisions = collisions, HasCollisions = collisions.Count > 0 };
    }
}
