using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridSlicer.Infrastructure.Resin;

/// <summary>
/// VATSlicer project file (.vatproj) — saves the complete workspace state.
/// This is a JSON file containing model references, transforms, manual supports,
/// support settings, and orientation data so user work is preserved across sessions.
/// </summary>
public sealed class ProjectFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("modifiedAt")] public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("models")] public List<ProjectModel> Models { get; set; } = new();
    [JsonPropertyName("printerId")] public string? PrinterId { get; set; }
    [JsonPropertyName("profileId")] public string? ProfileId { get; set; }
    [JsonPropertyName("supportOptions")] public JsonElement? SupportOptions { get; set; }

    public sealed class ProjectModel
    {
        [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
        [JsonPropertyName("stlBase64")] public string? StlBase64 { get; set; }
        // Transform
        [JsonPropertyName("positionX")] public float PositionX { get; set; }
        [JsonPropertyName("positionY")] public float PositionY { get; set; }
        [JsonPropertyName("positionZ")] public float PositionZ { get; set; }
        [JsonPropertyName("rotationX")] public float RotationX { get; set; }
        [JsonPropertyName("rotationY")] public float RotationY { get; set; }
        [JsonPropertyName("rotationZ")] public float RotationZ { get; set; }
        [JsonPropertyName("scale")] public float Scale { get; set; } = 1.0f;
        // Manual supports
        [JsonPropertyName("manualSupports")] public List<ProjectManualSupport> ManualSupports { get; set; } = new();
    }

    public sealed class ProjectManualSupport
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("z")] public float Z { get; set; }
        [JsonPropertyName("nx")] public float NX { get; set; }
        [JsonPropertyName("ny")] public float NY { get; set; }
        [JsonPropertyName("nz")] public float NZ { get; set; }
        [JsonPropertyName("tipDiameterMm")] public float TipDiameterMm { get; set; }
        [JsonPropertyName("shaftDiameterMm")] public float ShaftDiameterMm { get; set; }
        [JsonPropertyName("baseDiameterMm")] public float BaseDiameterMm { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "medium";
    }

    public string ToJson()
    {
        ModifiedAt = DateTimeOffset.UtcNow;
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    public static ProjectFile? FromJson(string json)
    {
        return JsonSerializer.Deserialize<ProjectFile>(json);
    }
}
