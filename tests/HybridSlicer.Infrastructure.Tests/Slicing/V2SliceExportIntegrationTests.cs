using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Exporters;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

/// <summary>
/// End-to-end integration: generate supports → extract slice elements →
/// rasterize layers → export to file format. Verifies the complete pipeline.
/// </summary>
public class V2SliceExportIntegrationTests
{
    private static StlMesh CreateOverhangModel()
    {
        var verts = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts.Add(a); verts.Add(b); verts.Add(c);
            verts.Add(a); verts.Add(c); verts.Add(d);
        }
        void AddBox(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            var v000 = new Vector3(x1, y1, z1); var v100 = new Vector3(x2, y1, z1);
            var v010 = new Vector3(x1, y2, z1); var v110 = new Vector3(x2, y2, z1);
            var v001 = new Vector3(x1, y1, z2); var v101 = new Vector3(x2, y1, z2);
            var v011 = new Vector3(x1, y2, z2); var v111 = new Vector3(x2, y2, z2);
            Quad(v001, v101, v111, v011); Quad(v000, v010, v110, v100);
            Quad(v100, v110, v111, v101); Quad(v000, v001, v011, v010);
            Quad(v010, v011, v111, v110); Quad(v000, v100, v101, v001);
        }
        AddBox(-3, -3, 0, 3, 3, 10);  // pillar
        AddBox(-12, -12, 8, 12, 12, 10); // overhang shelf
        AddBox(-4, -4, 18, 4, 4, 21);    // floating island

        int triCount = verts.Count / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
            var v0 = verts[t * 3]; var v1 = verts[t * 3 + 1]; var v2 = verts[t * 3 + 2];
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            float len = n.Length();
            if (len > 1e-6f) n /= len; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(data, off);
            BitConverter.GetBytes(n.Y).CopyTo(data, off + 4);
            BitConverter.GetBytes(n.Z).CopyTo(data, off + 8);
            off += 12;
            for (int v = 0; v < 3; v++)
            {
                BitConverter.GetBytes(verts[t * 3 + v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t * 3 + v].Y).CopyTo(data, off + 4);
                BitConverter.GetBytes(verts[t * 3 + v].Z).CopyTo(data, off + 8);
                off += 12;
            }
            off += 2;
        }
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void FullPipeline_GenerateSupports_SliceElements_ExportCtb()
    {
        // Step 1: Generate supports
        var mesh = CreateOverhangModel();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            EnableInterconnections = true,
        });

        result.ValidSupports.Should().BeGreaterThan(0, "model with overhang should produce supports");
        result.SliceElements.Should().NotBeEmpty("should have slice elements for printing");

        // Step 2: Rasterize layers from slice elements
        int resX = 96, resY = 54;
        float buildW = 96f, buildD = 54f;
        float layerHeight = 0.5f;
        float totalHeight = mesh.Max.Z - mesh.Min.Z + 5f;
        int layerCount = (int)Math.Ceiling(totalHeight / layerHeight);

        using var ctx = new LayerRasterizer.RenderContext(resX, resY, buildW, buildD, true, false, false);
        var layers = new List<byte[]>();

        for (int i = 0; i < layerCount; i++)
        {
            float z = layerHeight * (i + 0.5f);
            // Render support circles at this Z
            var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, z);
            ctx.Canvas.Clear(SkiaSharp.SKColors.Black);
            if (circles.Count > 0)
            {
                SupportSliceIntegrator.RenderSupportsOnLayer(
                    ctx.Canvas, circles, ctx.ScaleX, ctx.ScaleY,
                    resX / 2f, resY / 2f);
            }
            using var snap = ctx.Surface.Snapshot();
            using var pngData = snap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            var png = pngData.ToArray();
            layers.Add(png);
        }

        layers.Count.Should().BeGreaterThan(10, "should have many layers for a 21mm tall model");

        // Step 3: Export to CTB
        var config = new SliceExportConfig
        {
            ResolutionX = resX, ResolutionY = resY,
            BedWidthMm = buildW, BedDepthMm = buildD, BedHeightMm = 100,
            MachineName = "TestPrinter",
            LayerHeightMm = layerHeight,
            BottomLayerCount = 3,
            ExposureTimeS = 2f, BottomExposureTimeS = 30f,
            LiftDistanceMm = 5f, LiftSpeedMmPerMin = 120f,
            RetractSpeedMmPerMin = 240f, LightOffDelayS = 1f,
            BottomLiftDistanceMm = 8f, BottomLiftSpeedMmPerMin = 60f,
            TotalLayers = layers.Count,
        };

        var exporter = new CtbExporter();
        using var ms = new MemoryStream();
        exporter.Export(config, layers, ms);

        ms.Length.Should().BeGreaterThan(500, "CTB file with real layers should have substantial size");

        // Verify CTB magic
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        br.ReadUInt32().Should().Be(0x12FD_0086, "CTB magic number");
    }
}
