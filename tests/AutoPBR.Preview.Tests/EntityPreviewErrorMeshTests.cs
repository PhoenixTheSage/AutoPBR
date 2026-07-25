
namespace AutoPBR.Preview.Tests;

public sealed class EntityPreviewErrorMeshTests
{
  [Fact]
  public void Error_placeholder_mesh_has_exclamation_geometry_and_binds_texture()
  {
    const string path = "assets/minecraft/textures/entity/modded/mob.png";
    Assert.True(
      EntityModelRuntime.TryBuildErrorPlaceholderMesh(
        path,
        EntityModelRuntime.EntityPreviewErrorMeshReasons.UncataloguedEntityTexture,
        out var mesh,
        out var provenance));

    Assert.Equal(PreviewMeshDriverKind.ErrorPlaceholder, provenance.Kind);
    Assert.Equal(
      EntityModelRuntime.EntityPreviewErrorMeshReasons.UncataloguedEntityTexture,
      provenance.Detail);
    Assert.Equal(2, mesh.Elements.Count);
    Assert.True(mesh.UsesLivingEntityRendererColumnYFlip);
    Assert.Equal("entity/modded/mob", mesh.Textures["skin"]);
  }

  [Fact]
  public void Uncatalogued_entity_texture_resolves_to_error_placeholder_via_static_mesh()
  {
    var runtime = EntityModelRuntimeFactory.Create();
    var profile = new MinecraftNativeProfile(
        "26.1.2",
        Path.Combine(AppContext.BaseDirectory, "Data", "minecraft-native", "26.1.2"),
        new Version(26, 1, 2));
    const string path = "assets/minecraft/textures/entity/modded/mob.png";
    Assert.True(
        runtime.TryBuildStaticMesh(
            path,
            profile,
            idlePhase01: 0f,
            animationTimeSeconds: 0f,
            out var mesh,
            out var provenance));

    Assert.Equal(PreviewMeshDriverKind.ErrorPlaceholder, provenance.Kind);
    Assert.NotEmpty(mesh.Elements);
  }

}
