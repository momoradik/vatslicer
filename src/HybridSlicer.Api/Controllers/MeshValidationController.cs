using HybridSlicer.Infrastructure.Resin;
using Microsoft.AspNetCore.Mvc;

namespace HybridSlicer.Api.Controllers;

[ApiController]
[Route("api/mesh")]
public sealed class MeshValidationController : ControllerBase
{
    [HttpPost("validate")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> Validate([FromForm] IFormFile stlFile, CancellationToken ct)
    {
        if (stlFile is null || stlFile.Length == 0)
            return BadRequest("STL file is required.");

        byte[] data;
        using (var ms = new MemoryStream())
        {
            await stlFile.CopyToAsync(ms, ct);
            data = ms.ToArray();
        }

        try
        {
            var mesh = StlMesh.FromFile(data, stlFile.FileName);
            var result = MeshValidator.Validate(mesh);

            var size = mesh.Max - mesh.Min;

            // Compute surface area
            float surfaceArea = 0;
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                var v0 = mesh.Vertices[t * 3]; var v1 = mesh.Vertices[t * 3 + 1]; var v2 = mesh.Vertices[t * 3 + 2];
                surfaceArea += System.Numerics.Vector3.Cross(v1 - v0, v2 - v0).Length() * 0.5f;
            }

            return Ok(new
            {
                result.IsValid,
                result.TriangleCount,
                result.DegenerateTriangles,
                result.NanInfVertices,
                result.FlippedNormals,
                result.NonManifoldEdges,
                result.OpenEdges,
                result.BoundsValid,
                result.VolumeMm3,
                sizeX = size.X, sizeY = size.Y, sizeZ = size.Z,
                surfaceAreaMm2 = surfaceArea,
                result.Warnings,
                result.Errors,
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                IsValid = false,
                TriangleCount = 0,
                Warnings = Array.Empty<string>(),
                Errors = new[] { $"Failed to parse STL: {ex.Message}" },
            });
        }
    }
}
