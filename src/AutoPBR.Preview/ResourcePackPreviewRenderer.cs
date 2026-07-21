using System.IO.Compression;
using System.Numerics;

using AutoPBR.Core;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

namespace AutoPBR.Preview;

public static class ResourcePackPreviewRenderer
{
    /// <summary>
    /// Build a 2D composite preview (diffuse, normal, specular, height) for a single texture from the pack.
    /// Returns a PNG-encoded byte array suitable for UI display.
    /// </summary>
    public static async Task<PreviewRenderResult> RenderPreviewAsync(
        string inputZipPath,
        string archivePath,
        AutoPBROptions options,
        CancellationToken cancellationToken = default)
    {
        var detailed = await RenderPreviewDetailedAsync(inputZipPath, archivePath, options, cancellationToken)
            .ConfigureAwait(false);
        return new PreviewRenderResult(detailed.PngBytes, detailed.BrickProbeDebugText);
    }

    /// <summary>
    /// Same pipeline as <see cref="RenderPreviewAsync"/> but also returns raw RGBA maps for 3D preview before temp cleanup.
    /// </summary>
    public static async Task<PreviewDetailedResult> RenderPreviewDetailedAsync(
        string inputZipPath,
        string archivePath,
        AutoPBROptions options,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputZipPath))
        {
            throw new FileNotFoundException("Input pack not found.", inputZipPath);
        }

        if (options.SpecularData is null)
        {
            throw new InvalidOperationException("SpecularData is required (load textures_data.json first).");
        }

        var baseTemp = string.IsNullOrWhiteSpace(options.TempDirectory)
            ? Path.GetTempPath()
            : options.TempDirectory;
        var tempRoot = Path.Combine(baseTemp, "AutoPBR_Preview", Guid.NewGuid().ToString("N"));
        var extracted = Path.Combine(tempRoot, "pack_unzipped");
        Directory.CreateDirectory(extracted);

        try
        {
            PackExtractionService.ExtractEntry(inputZipPath, archivePath, extracted);

            cancellationToken.ThrowIfCancellationRequested();

            var previewNativeProfile = ResolveNativeMinecraftDataProfile(inputZipPath, extracted);
            MergedJavaBlockModel? mergedModel = null;
            List<string>? orderedModelTextures = null;
            string? modelDefaultNs = null;
            var isEmulatedEntityModel = false;
            PreviewMeshProvenance meshProvenance = default;
            using (var zip = ZipFile.OpenRead(inputZipPath))
            {
                var zipSource = new ZipAssetSource(zip);
                var nativeRoot = previewNativeProfile?.RootDirectory;
                using var assetSources = PreviewAssetSourceFactory.Create(
                    zipSource,
                    options.MinecraftAssetsDirectory,
                    nativeRoot);
                var resolved = RuntimeBlockPreviewModelResolver.Resolve(
                    zipSource,
                    assetSources,
                    archivePath,
                    extracted,
                    previewNativeProfile,
                    options);
                mergedModel = resolved.MergedModel;
                orderedModelTextures = resolved.OrderedModelTextures;
                modelDefaultNs = resolved.ModelDefaultNamespace;
                isEmulatedEntityModel = resolved.IsEmulatedEntityModel;
                meshProvenance = resolved.MeshProvenance;
            }

            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<TextureWorkItem> textures = TextureScanner.ScanTextures(
                extracted,
                options,
                cachePackPath: inputZipPath,
                applyFoliageIgnoreFilter: false,
                cancellationToken: cancellationToken);
            if (textures.Count == 0)
            {
                throw new InvalidOperationException("No previewable textures found after extraction.");
            }

            TextureWorkItem target = textures[0];
            var targetRel = archivePath.Replace('\\', '/');
            foreach (var t in textures)
            {
                var rel = Path.GetRelativePath(extracted, t.DiffusePath).Replace('\\', '/');
                if (string.Equals(rel, targetRel, StringComparison.OrdinalIgnoreCase))
                {
                    target = t;
                    break;
                }
            }

            if (mergedModel is not null && orderedModelTextures is not null && modelDefaultNs is not null)
            {
                var workOrdered = new List<TextureWorkItem>(orderedModelTextures.Count);
                foreach (var zpath in orderedModelTextures)
                {
                    var w = JavaModelPreviewPipeline.FindWorkItemByDiffuseZipPath(textures, extracted, zpath);
                    if (w is null)
                    {
                        workOrdered = null;
                        break;
                    }

                    workOrdered.Add(w);
                }

                if (workOrdered is null &&
                    TryRecoverBlockPreviewMaterials(
                        inputZipPath,
                        archivePath,
                        extracted,
                        previewNativeProfile,
                        options,
                        cancellationToken,
                        ref mergedModel,
                        ref orderedModelTextures,
                        ref modelDefaultNs,
                        ref textures,
                        ref meshProvenance,
                        out workOrdered))
                {
                    // Kept pack JSON geometry; filled missing sibling textures from install/native/catalog.
                }

                if (workOrdered is null &&
                    TryRecoverBlockPreviewWithVanillaParity(
                        inputZipPath,
                        archivePath,
                        extracted,
                        previewNativeProfile,
                        options,
                        cancellationToken,
                        ref mergedModel,
                        ref orderedModelTextures,
                        ref modelDefaultNs,
                        ref meshProvenance,
                        ref isEmulatedEntityModel,
                        ref textures,
                        out workOrdered))
                {
                    // Recovered via block texture parity catalog after pack JSON material gaps.
                }

                if (workOrdered is not null
                    && mergedModel is not null
                    && orderedModelTextures is not null
                    && modelDefaultNs is not null)
                {
                    await NormalHeightGenerator.GenerateAsync(workOrdered, options, null, cancellationToken)
                        .ConfigureAwait(false);
                    await SpecularGenerator.GenerateAsync(workOrdered, options, null, cancellationToken)
                        .ConfigureAwait(false);

                    cancellationToken.ThrowIfCancellationRequested();

                    var bakeProfile = ResolvePreviewMeshNativeProfile(previewNativeProfile);
                    var materials = new PreviewTextureMaps[workOrdered.Count];
                    var texSizes = new Dictionary<string, (int w, int h)>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < orderedModelTextures.Count; i++)
                    {
                        var loaded = PreviewTextureMapsLoader.Load(workOrdered[i]);
                        var size = EntityGeometryIrTextureAtlas.ResolveForBake(
                            orderedModelTextures[i],
                            loaded.Width,
                            loaded.Height,
                            meshProvenance,
                            bakeProfile);
                        texSizes[orderedModelTextures[i]] = (size.Width, size.Height);
                        materials[i] = new PreviewTextureMaps
                        {
                            Width = loaded.Width,
                            Height = loaded.Height,
                            BakeAtlasWidth = size.Width,
                            BakeAtlasHeight = size.Height,
                            DiffuseRgba = loaded.DiffuseRgba,
                            NormalRgba = loaded.NormalRgba,
                            SpecularRgba = loaded.SpecularRgba,
                            HeightRgba = loaded.HeightRgba,
                            IsPlantForNoHeight = loaded.IsPlantForNoHeight,
                            Sprite2DFoliageTarget = loaded.Sprite2DFoliageTarget,
                            IsItemTexturePath = loaded.IsItemTexturePath
                        };
                    }

                    var pathToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < orderedModelTextures.Count; i++)
                    {
                        pathToIdx[orderedModelTextures[i]] = i;
                    }

                    if (MinecraftModelBaker.TryBake(mergedModel, modelDefaultNs, pathToIdx, texSizes, out var verts,
                            out var indices, out var batchesList))
                    {
                        var normSel = archivePath.Replace('\\', '/').TrimStart('/');
                        var primIdx = orderedModelTextures.FindIndex(p =>
                            string.Equals(p, normSel, StringComparison.OrdinalIgnoreCase));
                        if (primIdx < 0)
                        {
                            primIdx = 0;
                        }

                        var primary = workOrdered[primIdx];
                        var sprite = primary.Sprite2DFoliageTarget;
                        EntityEmulatedPreviewRebakeContext? emulatedRebake = null;
                        var anchorOffset = Vector3.Zero;
                        var placementApplied = false;
                        var isParityCatalogEntity = EntityTextureParityCatalog.IsCatalogued(normSel);
                        if (isEmulatedEntityModel)
                        {
                            var prof = ResolvePreviewMeshNativeProfile(previewNativeProfile);
                            var idlePh = ComputeDeterministicIdlePhase(archivePath, prof.Name);
                            emulatedRebake = new EntityEmulatedPreviewRebakeContext
                            {
                                PackZipPath = inputZipPath,
                                AssetArchivePath = normSel,
                                NativeRootDirectory = prof.RootDirectory,
                                NativeProfileName = prof.Name,
                                NativeParsedVersion = prof.ParsedVersion?.ToString(),
                                ModelDefaultNamespace = modelDefaultNs,
                                IdlePhase01 = idlePh,
                                PreviewPoseId = EntityPreviewBuildContext.CurrentPoseId,
                                PreviewSizeId = EntityPreviewBuildContext.CurrentSizeId,
                                PreviewContextTypeId = EntityPreviewBuildContext.CurrentContextTypeId,
                                OrderedTextureZipPaths = orderedModelTextures.ToArray()
                            };
                            EntityPreviewPlacement.TryPopulateRebakeElementPartIds(
                                emulatedRebake,
                                prof,
                                mergedModel.Elements.Count);
                            EntityPreviewPlacement.TryMeasureMergedModelPartCentroidsY(
                                mergedModel,
                                emulatedRebake.ElementPartIds!,
                                out var bindBodyY,
                                out var bindHeadY,
                                out var bindLegY);
                            var placement = EntityPreviewPlacement.ApplyToPreviewVertices(
                                verts,
                                MinecraftModelBaker.FloatsPerVertex,
                                emulatedRebake.ElementPartIds!);
                            anchorOffset = placement.AnchorOffset;
                            placementApplied = true;
                            emulatedRebake.LastGroundContactY = placement.GroundContactY;
                            emulatedRebake.LastGroundLiftY = placement.GroundLiftY;
                            var placementYOffset = placement.AnchorOffset.Y + placement.GroundLiftY;
                            emulatedRebake.LastBodyCentroidY = bindBodyY != 0f ? bindBodyY + placementYOffset : placement.BodyCentroidY;
                            emulatedRebake.LastHeadCentroidY = bindHeadY != 0f ? bindHeadY + placementYOffset : placement.HeadCentroidY;
                            emulatedRebake.LastLegCentroidY = bindLegY != 0f ? bindLegY + placementYOffset : placement.LegCentroidY;
                            emulatedRebake.PackConverterCpuMeshFingerprint =
                                PreviewMeshGeometryFingerprint.ComputeCpuPreviewMesh(
                                    verts,
                                    MinecraftModelBaker.FloatsPerVertex);
                        }

                        var subject = new PreviewModelSubject
                        {
                            InterleavedVertices = verts,
                            Indices = indices,
                            DrawBatches = PreviewDrawBatchBounds.WithComputedBounds(
                                batchesList,
                                verts,
                                indices,
                                MinecraftModelBaker.FloatsPerVertex),
                            Materials = materials,
                            MaterialArchivePaths = orderedModelTextures.ToArray(),
                            PrimaryMaterialIndex = primIdx,
                            Sprite2DFoliageTarget = sprite,
                            EnableRenderTimeAnimation = isEmulatedEntityModel && !isParityCatalogEntity,
                            AnimationPreset = isEmulatedEntityModel ? "entity_emulated" : null,
                            EmulatedRebake = emulatedRebake,
                            MeshProvenance = meshProvenance,
                            EntityPreviewAnchorOffset = anchorOffset,
                            EntityPreviewPlacementApplied = placementApplied
                        };

                        if (emulatedRebake is not null)
                        {
                            emulatedRebake.MeshProvenance = meshProvenance;
                        }

                        var png = PreviewComposer.ComposePreview(primary);
                        var dbg = options.BrickProbePreviewDebug ? primary.BrickProbeDebugText : null;
                        var previewSubject = PreviewPathPolicy.ShouldUseFlatItemPlane(normSel, sprite)
                            ? null
                            : subject;
                        return new PreviewDetailedResult(png, materials[primIdx], dbg, previewSubject, meshProvenance);
                    }
                }
            }

            var single = new List<TextureWorkItem> { target };

            await NormalHeightGenerator.GenerateAsync(single, options, null, cancellationToken).ConfigureAwait(false);
            await SpecularGenerator.GenerateAsync(single, options, null, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var legacyPng = PreviewComposer.ComposePreview(target);
            var legacyMaps = PreviewTextureMapsLoader.Load(target);
            var legacyDbg = options.BrickProbePreviewDebug ? target.BrickProbeDebugText : null;
            return new PreviewDetailedResult(legacyPng, legacyMaps, legacyDbg);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }

    private static bool TryRecoverBlockPreviewMaterials(
        string inputZipPath,
        string archivePath,
        string extracted,
        MinecraftNativeProfile? previewNativeProfile,
        AutoPBROptions options,
        CancellationToken cancellationToken,
        ref MergedJavaBlockModel? mergedModel,
        ref List<string>? orderedModelTextures,
        ref string? modelDefaultNs,
        ref IReadOnlyList<TextureWorkItem> textures,
        ref PreviewMeshProvenance meshProvenance,
        out List<TextureWorkItem>? workOrdered)
    {
        workOrdered = null;
        if (mergedModel is null || orderedModelTextures is null || modelDefaultNs is null)
        {
            return false;
        }

        using var zip = ZipFile.OpenRead(inputZipPath);
        using var assetSources = PreviewAssetSourceFactory.Create(
            new ZipAssetSource(zip),
            options.MinecraftAssetsDirectory,
            previewNativeProfile?.RootDirectory);
        var ordered = orderedModelTextures;
        if (!BlockPreviewMaterialRecovery.TryRecoverBlockPreviewMaterials(
                assetSources,
                archivePath,
                extracted,
                mergedModel,
                modelDefaultNs,
                ref ordered,
                ref textures,
                options,
                cancellationToken))
        {
            return false;
        }

        orderedModelTextures = ordered;
        meshProvenance = PreviewProvenanceFormatter.WithTag(meshProvenance, "material-recovery");
        workOrdered = new List<TextureWorkItem>(ordered.Count);
        foreach (var zpath in ordered)
        {
            var w = JavaModelPreviewPipeline.FindWorkItemByDiffuseZipPath(textures, extracted, zpath);
            if (w is null)
            {
                workOrdered = null;
                return false;
            }

            workOrdered.Add(w);
        }

        return true;
    }

    private static bool TryRecoverBlockPreviewWithVanillaParity(
        string inputZipPath,
        string archivePath,
        string extracted,
        MinecraftNativeProfile? previewNativeProfile,
        AutoPBROptions options,
        CancellationToken cancellationToken,
        ref MergedJavaBlockModel? mergedModel,
        ref List<string>? orderedModelTextures,
        ref string? modelDefaultNs,
        ref PreviewMeshProvenance meshProvenance,
        ref bool isEmulatedEntityModel,
        ref IReadOnlyList<TextureWorkItem> textures,
        out List<TextureWorkItem>? workOrdered)
    {
        workOrdered = null;
        if (!VanillaBlockPreviewRuntime.IsBlockTextureArchivePath(archivePath) ||
            !VanillaBlockPreviewRuntime.TryBuildSyntheticMesh(
                archivePath,
                out var parityModel,
                out var parityProvenance,
                out var parityOrdered,
                out var parityNs))
        {
            return false;
        }

        using (var zip = ZipFile.OpenRead(inputZipPath))
        {
            var zipSource = new ZipAssetSource(zip);
            var nativeRoot = previewNativeProfile?.RootDirectory;
            using var assetSources = PreviewAssetSourceFactory.Create(
                zipSource,
                options.MinecraftAssetsDirectory,
                nativeRoot);
            foreach (var asset in parityOrdered)
            {
                if (assetSources.Composite.Exists(asset))
                {
                    AssetSourceMaterializer.Materialize(assetSources.Composite, asset, extracted);
                }
            }
        }

        textures = TextureScanner.ScanTextures(
            extracted,
            options,
            applyFoliageIgnoreFilter: false,
            cancellationToken: cancellationToken);
        mergedModel = parityModel;
        orderedModelTextures = parityOrdered;
        modelDefaultNs = parityNs;
        meshProvenance = parityProvenance;
        meshProvenance = PreviewProvenanceFormatter.WithTag(meshProvenance, "parity-recovery");
        isEmulatedEntityModel = false;

        var assembled = new List<TextureWorkItem>(parityOrdered.Count);
        foreach (var zpath in parityOrdered)
        {
            var w = JavaModelPreviewPipeline.FindWorkItemByDiffuseZipPath(textures, extracted, zpath);
            if (w is null)
            {
                return false;
            }

            assembled.Add(w);
        }

        workOrdered = assembled;
        return true;
    }

    private static MinecraftNativeProfile? ResolveNativeMinecraftDataProfile(string inputZipPath, string extractedPackDir)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Data", "minecraft-native");
        if (!Directory.Exists(root))
        {
            return null;
        }

        return MinecraftNativeProfileResolver.ResolveForPreview(root, inputZipPath, extractedPackDir);
    }

    /// <summary>
    /// Entity mesh / rebake must never use the placeholder <c>unknown</c> profile (blocks geometry IR under <c>geometry/unknown/</c>).
    /// </summary>
    private static MinecraftNativeProfile ResolvePreviewMeshNativeProfile(MinecraftNativeProfile? resolved)
    {
        if (resolved is { Name: var n } && NativeIrVersionLabels.IsRecognizedProfileName(n))
        {
            return resolved;
        }

        var nativeRoot = Path.Combine(AppContext.BaseDirectory, "Data", "minecraft-native");
        return MinecraftNativeProfileResolver.ResolveAutoLatestModern(nativeRoot)
               ?? MinecraftNativeProfileResolver.ResolveAutoLatest(nativeRoot)
               ?? new MinecraftNativeProfile(
                   NativeIrVersionLabels.ModernGeometryLabel,
                   nativeRoot,
                   new Version(26, 1, 2));
    }

    private static bool IsEntityTextureArchivePath(string archivePath) =>
        archivePath.Replace('\\', '/').Contains("/textures/entity/", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetAssetNamespace(string archivePath)
    {
        var parts = archivePath.Replace('\\', '/').TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && parts[0].Equals("assets", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    private static float ComputeDeterministicIdlePhase(string archivePath, string profileName)
    {
        var s = archivePath + "|" + profileName;
        unchecked
        {
            var h = 17;
            foreach (var ch in s)
            {
                h = (h * 31) + ch;
            }

            return ((h & 0x7fffffff) % 1000) / 1000f;
        }
    }

    /// <summary>
    /// Stable animation phase for explore / initial bake so "Set Preview" does not jump keyframes.
    /// Render-tab playback uses wall-clock time via <see cref="EntityEmulatedPreviewRebaker"/>.
    /// </summary>
    private static float ComputeDeterministicAnimationTimeSeconds(string archivePath, string profileName)
    {
        var s = archivePath + "|anim|" + profileName;
        unchecked
        {
            var h = 19;
            foreach (var ch in s)
            {
                h = (h * 31) + ch;
            }

            // 0..120s window — enough for multi-clip breeze cycles without wrapping too fast.
            return ((h & 0x7fffffff) % 120_000) / 1000f;
        }
    }

}

