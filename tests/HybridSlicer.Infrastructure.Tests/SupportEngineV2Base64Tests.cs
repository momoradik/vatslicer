using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests for the base64 STL encoding used in the API response.
/// The frontend decodes this to render the V2 mesh in Three.js.
/// </summary>
public class SupportEngineV2Base64Tests
{
    private static SupportEngineV2.EngineResult Generate()
    {
        var o = new Vector3(-10, -10, 10);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 20f;
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));
        int triCount = verts.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++) {
            off += 12;
            for (int v = 0; v < 3; v++) {
                BitConverter.GetBytes(verts[t*3+v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t*3+v].Y).CopyTo(data, off+4);
                BitConverter.GetBytes(verts[t*3+v].Z).CopyTo(data, off+8);
                off += 12;
            }
            off += 2;
        }
        return SupportEngineV2.Generate(StlMesh.FromBinary(data), new SupportEngineV2.EngineConfig());
    }

    [Fact]
    public void Base64_Encode_Decode_Roundtrip()
    {
        var result = Generate();
        var stl = result.SupportMesh.ToStlBinary();
        var base64 = Convert.ToBase64String(stl);
        var decoded = Convert.FromBase64String(base64);

        decoded.Length.Should().Be(stl.Length);
        for (int i = 0; i < stl.Length; i++)
            decoded[i].Should().Be(stl[i], $"byte {i} should match");
    }

    [Fact]
    public void Base64_DecodedSTL_CanBeReimported()
    {
        var result = Generate();
        var stl = result.SupportMesh.ToStlBinary();
        var base64 = Convert.ToBase64String(stl);
        var decoded = Convert.FromBase64String(base64);

        var reimported = StlMesh.FromBinary(decoded);
        reimported.TriangleCount.Should().Be(result.SupportMesh.FaceCount);
    }

    [Fact]
    public void Base64_Size_IsReasonable()
    {
        var result = Generate();
        var stl = result.SupportMesh.ToStlBinary();
        var base64 = Convert.ToBase64String(stl);

        // Base64 is ~33% larger than binary
        float ratio = (float)base64.Length / stl.Length;
        ratio.Should().BeInRange(1.3f, 1.4f, "base64 should be ~33% larger");
    }

    [Fact]
    public void Base64_IsValidString()
    {
        var result = Generate();
        var stl = result.SupportMesh.ToStlBinary();
        var base64 = Convert.ToBase64String(stl);

        base64.Should().NotBeNullOrEmpty();
        base64.Should().MatchRegex(@"^[A-Za-z0-9+/]+=*$", "valid base64 characters only");
    }
}
