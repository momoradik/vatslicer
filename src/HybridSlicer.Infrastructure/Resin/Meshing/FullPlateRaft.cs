using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Meshing;

/// <summary>
/// Generates a full-plate raft: a raised wall structure on the build plate.
///
/// Structure (ChiTuBox Pro style):
/// 1. Solid base plate (thin, for bed adhesion) — Z=0 to Z=baseThickness
/// 2. Vertical walls rising from the base plate forming a grid or honeycomb pattern
///    — Z=baseThickness to Z=baseThickness+wallHeight
/// 3. Supports land ON TOP of the wall intersections
///
/// Grid pattern: orthogonal walls forming rectangular open cells (waffle/egg-crate)
/// Hex pattern: hexagonal walls forming honeycomb open cells
///
/// The open cells allow resin drainage and reduce peel forces vs a solid slab.
/// </summary>
public static class FullPlateRaft
{
    public enum Pattern { Grid, Hex }

    /// <summary>
    /// Generate a full-plate raft mesh with raised wall structure.
    /// </summary>
    public static IndexedTriangleSet Generate(
        float minX, float minY, float maxX, float maxY,
        float baseThickness = 0.3f,
        float wallHeight = 3.0f,
        float wallThickness = 0.4f,
        float cellSize = 3.0f,
        Pattern pattern = Pattern.Grid)
    {
        var mesh = new IndexedTriangleSet();
        float w = maxX - minX;
        float d = maxY - minY;
        if (w < 1f || d < 1f) return mesh;

        float wallBot = baseThickness;
        float wallTop = baseThickness + wallHeight;

        // 1. Solid base plate (flat box for adhesion)
        AddBox(mesh, minX, minY, 0f, maxX, maxY, baseThickness);

        // 2. Walls
        if (pattern == Pattern.Grid)
            GenerateGridWalls(mesh, minX, minY, maxX, maxY, wallBot, wallTop, wallThickness, cellSize);
        else
            GenerateHexWalls(mesh, minX, minY, maxX, maxY, wallBot, wallTop, wallThickness, cellSize);

        return mesh;
    }

    // ── Grid pattern: orthogonal walls ──────────────────────────────────

    private static void GenerateGridWalls(IndexedTriangleSet mesh,
        float minX, float minY, float maxX, float maxY,
        float zBot, float zTop, float wallThickness, float cellSize)
    {
        float step = cellSize + wallThickness;

        // Perimeter walls
        AddWall(mesh, minX, minY, maxX, minY, zBot, zTop, wallThickness); // front
        AddWall(mesh, minX, maxY, maxX, maxY, zBot, zTop, wallThickness); // back
        AddWall(mesh, minX, minY, minX, maxY, zBot, zTop, wallThickness); // left
        AddWall(mesh, maxX, minY, maxX, maxY, zBot, zTop, wallThickness); // right

        // X-running internal walls (at Y intervals)
        for (float y = minY + step; y < maxY - wallThickness; y += step)
            AddWall(mesh, minX, y, maxX, y, zBot, zTop, wallThickness);

        // Y-running internal walls (at X intervals)
        for (float x = minX + step; x < maxX - wallThickness; x += step)
            AddWall(mesh, x, minY, x, maxY, zBot, zTop, wallThickness);
    }

    // ── Hex pattern: honeycomb walls ────────────────────────────────────

    private static void GenerateHexWalls(IndexedTriangleSet mesh,
        float minX, float minY, float maxX, float maxY,
        float zBot, float zTop, float wallThickness, float cellSize)
    {
        // Flat-topped hexagons: vertex at 0°, 60°, 120°, 180°, 240°, 300°
        // Scale hexR so we get ~10-15 hexes per axis (visible, not a dense mass).
        // Target: footprint / hexR ≈ 12-15 cells across the larger dimension.
        float footprintMax = Math.Max(maxX - minX, maxY - minY);
        float hexR = Math.Max(cellSize * 2.5f, footprintMax / 14f);
        float colStep = hexR * 1.5f;
        float rowStep = hexR * MathF.Sqrt(3f);

        // Track edges to avoid duplicates (quantized vertex pairs)
        var edges = new HashSet<long>();

        // Perimeter
        AddWall(mesh, minX, minY, maxX, minY, zBot, zTop, wallThickness);
        AddWall(mesh, minX, maxY, maxX, maxY, zBot, zTop, wallThickness);
        AddWall(mesh, minX, minY, minX, maxY, zBot, zTop, wallThickness);
        AddWall(mesh, maxX, minY, maxX, maxY, zBot, zTop, wallThickness);

        for (float cx = minX + hexR; cx < maxX; cx += colStep)
        {
            int col = (int)MathF.Round((cx - minX) / colStep);
            float yOff = (col % 2 == 0) ? 0f : rowStep * 0.5f;

            for (float cy = minY + hexR + yOff; cy < maxY; cy += rowStep)
            {
                // Generate 6 edges of this hexagon
                for (int i = 0; i < 6; i++)
                {
                    float a1 = MathF.PI / 3f * i;
                    float a2 = MathF.PI / 3f * ((i + 1) % 6);

                    float vx1 = cx + hexR * MathF.Cos(a1);
                    float vy1 = cy + hexR * MathF.Sin(a1);
                    float vx2 = cx + hexR * MathF.Cos(a2);
                    float vy2 = cy + hexR * MathF.Sin(a2);

                    // Clip to bounds
                    if (vx1 < minX - 1 || vx1 > maxX + 1 || vy1 < minY - 1 || vy1 > maxY + 1) continue;
                    if (vx2 < minX - 1 || vx2 > maxX + 1 || vy2 < minY - 1 || vy2 > maxY + 1) continue;

                    // Deduplicate edges by quantizing vertex positions (0.1mm resolution)
                    long k1 = QuantizeKey(vx1, vy1);
                    long k2 = QuantizeKey(vx2, vy2);
                    long edgeKey = k1 < k2 ? k1 * 10_000_000L + k2 : k2 * 10_000_000L + k1;
                    if (!edges.Add(edgeKey)) continue;

                    AddWall(mesh, vx1, vy1, vx2, vy2, zBot, zTop, wallThickness);
                }
            }
        }
    }

    private static long QuantizeKey(float x, float y)
    {
        // Quantize to 0.1mm grid for deduplication
        int qx = (int)MathF.Round(x * 10f);
        int qy = (int)MathF.Round(y * 10f);
        return (long)(qx + 100000) * 100000L + (qy + 100000);
    }

    // ── Geometry helpers ────────────────────────────────────────────────

    /// <summary>
    /// Add a vertical wall as a thin rectangular prism.
    /// Wall runs from (x1,y1) to (x2,y2) with given thickness, Z from zBot to zTop.
    /// </summary>
    private static void AddWall(IndexedTriangleSet mesh,
        float x1, float y1, float x2, float y2,
        float zBot, float zTop, float thickness)
    {
        float dx = x2 - x1, dy = y2 - y1;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.01f) return;

        // Perpendicular offset (half-thickness on each side)
        float ht = thickness * 0.5f;
        float nx = -dy / len * ht;
        float ny = dx / len * ht;

        // 8 vertices of the wall box
        int v0 = mesh.AddVertex(new Vector3(x1 - nx, y1 - ny, zBot));
        int v1 = mesh.AddVertex(new Vector3(x1 + nx, y1 + ny, zBot));
        int v2 = mesh.AddVertex(new Vector3(x2 + nx, y2 + ny, zBot));
        int v3 = mesh.AddVertex(new Vector3(x2 - nx, y2 - ny, zBot));
        int v4 = mesh.AddVertex(new Vector3(x1 - nx, y1 - ny, zTop));
        int v5 = mesh.AddVertex(new Vector3(x1 + nx, y1 + ny, zTop));
        int v6 = mesh.AddVertex(new Vector3(x2 + nx, y2 + ny, zTop));
        int v7 = mesh.AddVertex(new Vector3(x2 - nx, y2 - ny, zTop));

        // Front face (normal pointing outward in -perp direction)
        mesh.AddFace(v0, v3, v7); mesh.AddFace(v0, v7, v4);
        // Back face
        mesh.AddFace(v1, v5, v6); mesh.AddFace(v1, v6, v2);
        // Top face
        mesh.AddFace(v4, v7, v6); mesh.AddFace(v4, v6, v5);
        // Bottom face
        mesh.AddFace(v0, v2, v3); mesh.AddFace(v0, v1, v2);
        // Left end
        mesh.AddFace(v0, v4, v5); mesh.AddFace(v0, v5, v1);
        // Right end
        mesh.AddFace(v3, v2, v6); mesh.AddFace(v3, v6, v7);
    }

    /// <summary>
    /// Add an axis-aligned box (6 faces, 12 tris).
    /// </summary>
    private static void AddBox(IndexedTriangleSet mesh,
        float x0, float y0, float z0, float x1, float y1, float z1)
    {
        int v0 = mesh.AddVertex(new Vector3(x0, y0, z0));
        int v1 = mesh.AddVertex(new Vector3(x1, y0, z0));
        int v2 = mesh.AddVertex(new Vector3(x1, y1, z0));
        int v3 = mesh.AddVertex(new Vector3(x0, y1, z0));
        int v4 = mesh.AddVertex(new Vector3(x0, y0, z1));
        int v5 = mesh.AddVertex(new Vector3(x1, y0, z1));
        int v6 = mesh.AddVertex(new Vector3(x1, y1, z1));
        int v7 = mesh.AddVertex(new Vector3(x0, y1, z1));

        // Bottom (-Z)
        mesh.AddFace(v0, v2, v1); mesh.AddFace(v0, v3, v2);
        // Top (+Z)
        mesh.AddFace(v4, v5, v6); mesh.AddFace(v4, v6, v7);
        // Front (-Y)
        mesh.AddFace(v0, v1, v5); mesh.AddFace(v0, v5, v4);
        // Back (+Y)
        mesh.AddFace(v3, v7, v6); mesh.AddFace(v3, v6, v2);
        // Left (-X)
        mesh.AddFace(v0, v4, v7); mesh.AddFace(v0, v7, v3);
        // Right (+X)
        mesh.AddFace(v1, v2, v6); mesh.AddFace(v1, v6, v5);
    }

    // ── Skate raft ──────────────────────────────────────────────────────

    /// <summary>
    /// Generate a skate raft: one solid connected mat filling the footprint.
    /// The outer wall is sloped at RaftSlopeDeg, forming a raised peel edge
    /// (top edge inset from bottom edge). The interior top surface is flat.
    ///
    /// Cross-section (side view):
    ///         ┌───────────────┐  ← top surface (inset from bottom by lip)
    ///        /                 \  ← sloped outer wall at RaftSlopeDeg
    ///       └───────────────────┘ ← bottom on build plate (full footprint)
    /// </summary>
    public static IndexedTriangleSet GenerateSkate(
        float minX, float minY, float maxX, float maxY,
        float thickness = 1.0f, float slopeDeg = 45f)
    {
        var mesh = new IndexedTriangleSet();
        float w = maxX - minX, d = maxY - minY;
        if (w < 1f || d < 1f || thickness < 0.05f) return mesh;

        // The lip inset: how far the top edge is inset from the bottom edge
        // lip = thickness / tan(slope). At 45° → lip = thickness.
        float slopeRad = slopeDeg * MathF.PI / 180f;
        float tanSlope = MathF.Tan(slopeRad);
        float lip = tanSlope > 0.01f ? thickness / tanSlope : thickness;
        lip = Math.Min(lip, Math.Min(w, d) * 0.4f); // don't inset more than 40%

        float topZ = thickness;

        // Bottom corners (full footprint at Z=0)
        int b0 = mesh.AddVertex(new Vector3(minX, minY, 0));
        int b1 = mesh.AddVertex(new Vector3(maxX, minY, 0));
        int b2 = mesh.AddVertex(new Vector3(maxX, maxY, 0));
        int b3 = mesh.AddVertex(new Vector3(minX, maxY, 0));

        // Top corners (inset by lip at Z=topZ)
        int t0 = mesh.AddVertex(new Vector3(minX + lip, minY + lip, topZ));
        int t1 = mesh.AddVertex(new Vector3(maxX - lip, minY + lip, topZ));
        int t2 = mesh.AddVertex(new Vector3(maxX - lip, maxY - lip, topZ));
        int t3 = mesh.AddVertex(new Vector3(minX + lip, maxY - lip, topZ));

        // Bottom face (Z=0, normal -Z)
        mesh.AddFace(b0, b2, b1); mesh.AddFace(b0, b3, b2);

        // Top face (Z=topZ, normal +Z)
        mesh.AddFace(t0, t1, t2); mesh.AddFace(t0, t2, t3);

        // Sloped outer walls (4 sides, each a trapezoid = 2 tris)
        // Front (-Y): b0,b1 (bottom) → t0,t1 (top, inset)
        mesh.AddFace(b0, b1, t1); mesh.AddFace(b0, t1, t0);
        // Back (+Y): b3,b2 (bottom) → t3,t2 (top, inset)
        mesh.AddFace(b2, b3, t3); mesh.AddFace(b2, t3, t2);
        // Left (-X): b0,b3 (bottom) → t0,t3 (top, inset)
        mesh.AddFace(b3, b0, t0); mesh.AddFace(b3, t0, t3);
        // Right (+X): b1,b2 (bottom) → t1,t2 (top, inset)
        mesh.AddFace(b1, b2, t2); mesh.AddFace(b1, t2, t1);

        return mesh;
    }
}
