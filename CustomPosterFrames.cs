using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using SFB;
using SteamShelf;
using SteamShelf.Placeables;
using SteamShelf.Save;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Boxroom_Plus.Patches
{
    /*
     * Drop-in custom poster support for BOXROOM frames.
     *
     * Current behavior:
     * - Alt + Shift + Click a GameImagePainter frame while PaintArt is called opens a file picker.
     * - Selected image is copied to:
     *   AppData/LocalLow/NestedLoop/BOXROOM/Boxroom-Plus/Posters/
     * - The copied filename is stored in the frame's CustomData as boxroomPlusPoster.
     * - On load / repaint, the custom poster texture overrides vanilla box art / screenshots.
     *
     * Note:
     * This patches GameImagePainter.PaintArt, so it works for interactions that call PaintArt.
     * If BOXROOM has a separate empty-hand frame interaction, patch that later and call
     * CustomPosterFrameManager.OpenPosterPicker(__instance).
     */

    internal class PainterSaveDataPlus
    {
        public int appId;
        public int screenshotIndex;
        public string boxroomPlusPoster;
    }

    internal static class CustomPosterFrameManager
    {
        private static readonly Dictionary<string, Texture2D> TextureCache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private static readonly FieldInfo TargetRendererField =
            AccessTools.Field(typeof(GameImagePainter), "targetRenderer");

        private static readonly FieldInfo MaterialIndexField =
            AccessTools.Field(typeof(GameImagePainter), "materialIndex");

        private static readonly FieldInfo OverrideMaterialField =
            AccessTools.Field(typeof(GameImagePainter), "overrideMaterial");

        private static readonly FieldInfo CurrentSaveStateField =
            AccessTools.Field(typeof(GameImagePainter), "currentSaveState");

        public static bool IsShiftHeld()
        {
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        }

        public static bool IsFrame(GameImagePainter painter)
        {
            if (painter == null)
                return false;

            PlacementTag tag = painter.GetComponent<PlacementTag>();

            if (tag == null || tag.PlaceableData == null)
                return false;

            string id = tag.PlaceableData.ID ?? "";

            return id.StartsWith("Placeable_frame_", StringComparison.OrdinalIgnoreCase);
        }

        public static void OpenPosterPicker(GameImagePainter painter)
        {
            try
            {
                if (painter == null || !IsFrame(painter))
                    return;

                Directory.CreateDirectory(GetPosterFolder());

                ExtensionFilter[] filters = new ExtensionFilter[]
                {
                    new ExtensionFilter("Images", "png", "jpg", "jpeg")
                };

                string[] files = StandaloneFileBrowser.OpenFilePanel(
                    "Select Custom Poster",
                    "",
                    filters,
                    false);

                if (files == null || files.Length == 0)
                    return;

                string source = files[0];

                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                    return;

                string ext = Path.GetExtension(source);

                if (string.IsNullOrWhiteSpace(ext))
                    ext = ".png";

                string fileName = Guid.NewGuid().ToString("N") + ext.ToLowerInvariant();
                string dest = Path.Combine(GetPosterFolder(), fileName);

                File.Copy(source, dest, true);

                SetPoster(painter, fileName);
                ApplyPoster(painter, fileName);

                MelonLogger.Msg("[Boxroom Plus Posters] Custom poster applied: " + fileName);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[Boxroom Plus Posters] OpenPosterPicker failed: " + ex);
            }
        }

        public static bool TryApplySavedPoster(GameImagePainter painter)
        {
            if (painter == null || !IsFrame(painter))
                return false;

            string poster = GetPoster(painter);

            if (string.IsNullOrWhiteSpace(poster))
                return false;

            return ApplyPoster(painter, poster);
        }

        private static bool ApplyPoster(GameImagePainter painter, string posterFile)
        {
            try
            {
                Texture2D texture = LoadPosterTexture(posterFile);

                if (texture == null)
                    return false;

                Renderer renderer = TargetRendererField.GetValue(painter) as Renderer;

                if (renderer == null)
                    return false;

                int materialIndex = (int)MaterialIndexField.GetValue(painter);

                Material overrideMaterial = OverrideMaterialField.GetValue(painter) as Material;

                if (overrideMaterial != null)
                    MaterialHelpers.SetMaterial(renderer, materialIndex, overrideMaterial);

                MaterialHelpers.SetTexture(renderer, materialIndex, texture);

                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[Boxroom Plus] ApplyPoster failed: " + ex);
                return false;
            }
        }

        private static void SetPoster(GameImagePainter painter, string posterFile)
        {
            PlaceableSaveState saveState = GetOrCreateSaveState(painter);

            if (saveState == null)
                return;

            PainterSaveDataPlus data = ReadPlusData(saveState.CustomData);

            if (data == null)
                data = new PainterSaveDataPlus();

            if (data.appId <= 0)
            {
                PainterSaveData vanilla = ReadVanillaData(saveState.CustomData);

                if (vanilla != null)
                {
                    data.appId = vanilla.appId;
                    data.screenshotIndex = vanilla.screenshotIndex;
                }
            }

            data.boxroomPlusPoster = posterFile;

            saveState.CustomData = JsonConvert.SerializeObject(data);
        }

        private static string GetPoster(GameImagePainter painter)
        {
            PlaceableSaveState saveState = GetSaveState(painter);

            if (saveState == null || string.IsNullOrWhiteSpace(saveState.CustomData))
                return "";

            PainterSaveDataPlus data = ReadPlusData(saveState.CustomData);

            if (data == null)
                return "";

            return data.boxroomPlusPoster ?? "";
        }

        private static PlaceableSaveState GetSaveState(GameImagePainter painter)
        {
            if (painter == null)
                return null;

            return CurrentSaveStateField.GetValue(painter) as PlaceableSaveState;
        }

        private static PlaceableSaveState GetOrCreateSaveState(GameImagePainter painter)
        {
            PlaceableSaveState saveState = GetSaveState(painter);

            if (saveState != null)
                return saveState;

            saveState = new PlaceableSaveState(
                "",
                painter.transform.position,
                painter.transform.rotation);

            CurrentSaveStateField.SetValue(painter, saveState);

            return saveState;
        }

        private static PainterSaveDataPlus ReadPlusData(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<PainterSaveDataPlus>(json);
            }
            catch
            {
                return null;
            }
        }

        private static PainterSaveData ReadVanillaData(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonUtility.FromJson<PainterSaveData>(json);
            }
            catch
            {
                return null;
            }
        }

        private static Texture2D LoadPosterTexture(string posterFile)
        {
            if (string.IsNullOrWhiteSpace(posterFile))
                return null;

            Texture2D cached;

            if (TextureCache.TryGetValue(posterFile, out cached))
                return cached;

            string path = Path.Combine(GetPosterFolder(), posterFile);

            if (!File.Exists(path))
            {
                MelonLogger.Warning("[Boxroom Plus Posters] Poster file missing: " + path);
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);

            Texture2D texture = new Texture2D(2, 2);

            if (!RuntimeImageLoader.LoadImage(texture, bytes))
            {
                UnityEngine.Object.Destroy(texture);
                MelonLogger.Warning("[Boxroom Plus Posters] Failed to decode poster: " + path);
                return null;
            }

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.Apply(false, true);

            TextureCache[posterFile] = texture;

            return texture;
        }

        private static string GetPosterFolder()
        {
            string root = Path.GetFullPath(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "..",
                    "LocalLow",
                    "NestedLoop",
                    "BOXROOM",
                    "Boxroom-Plus"));

            return Path.Combine(root, "Posters");
        }
    }

    internal static class RuntimeImageLoader
    {
        private static MethodInfo LoadImageMethod;

        static RuntimeImageLoader()
        {
            try
            {
                Assembly asm = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "UnityEngine.ImageConversionModule");

                if (asm == null)
                {
                    MelonLogger.Warning("[Boxroom Plus] ImageConversionModule not loaded.");
                    return;
                }

                Type imageConversion = asm.GetType("UnityEngine.ImageConversion");

                if (imageConversion == null)
                {
                    MelonLogger.Warning("[Boxroom Plus] UnityEngine.ImageConversion not found.");
                    return;
                }

                foreach (MethodInfo method in imageConversion.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "LoadImage")
                        continue;

                    ParameterInfo[] p = method.GetParameters();

                    if (p.Length >= 2 &&
                        p[0].ParameterType == typeof(Texture2D) &&
                        p[1].ParameterType == typeof(byte[]))
                    {
                        LoadImageMethod = method;
                        break;
                    }
                }

                if (LoadImageMethod != null)
                    MelonLogger.Msg("[Boxroom Plus Posters] ImageConversion.LoadImage hooked via reflection.");
                else
                    MelonLogger.Warning("[Boxroom Plus Posters] Failed to locate ImageConversion.LoadImage.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[Boxroom Plus Posters] RuntimeImageLoader init failed: " + ex);
            }
        }

        public static bool LoadImage(Texture2D texture, byte[] bytes)
        {
            if (LoadImageMethod == null || texture == null || bytes == null)
                return false;

            try
            {
                ParameterInfo[] p = LoadImageMethod.GetParameters();

                object result;

                if (p.Length == 2)
                {
                    result = LoadImageMethod.Invoke(null, new object[]
                    {
                        texture,
                        bytes
                    });
                }
                else
                {
                    result = LoadImageMethod.Invoke(null, new object[]
                    {
                        texture,
                        bytes,
                        false
                    });
                }

                return result is bool && (bool)result;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[Boxroom Plus Posters] RuntimeImageLoader.LoadImage failed: " + ex);
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(GameImagePainter), nameof(GameImagePainter.PaintArt))]
    internal static class CustomPosterPaintArtPatch
    {
        static bool Prefix(GameImagePainter __instance, SteamGameData game)
        {
            if (__instance == null)
                return true;

            if (!CustomPosterFrameManager.IsFrame(__instance))
                return true;

            if (!CustomPosterFrameManager.IsShiftHeld())
                return true;

            CustomPosterFrameManager.OpenPosterPicker(__instance);

            return false;
        }
    }

    [HarmonyPatch(typeof(GameImagePainter), "ApplyArt")]
    internal static class CustomPosterApplyArtPatch
    {
        static bool Prefix(GameImagePainter __instance, SteamGameData game)
        {
            if (__instance == null)
                return true;

            if (CustomPosterFrameManager.TryApplySavedPoster(__instance))
                return false;

            return true;
        }
    }

    [HarmonyPatch(typeof(GameImagePainter), nameof(GameImagePainter.PopulateFromLoad))]
    internal static class CustomPosterPopulatePatch
    {
        static void Postfix(GameImagePainter __instance, PlaceableSaveState saveState)
        {
            if (__instance == null)
                return;

            CustomPosterFrameManager.TryApplySavedPoster(__instance);
        }
    }
}
