using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// A single image staged for the next assistant turn (Sprint 73): its base64 PNG/JPEG bytes, media type,
    /// pixel dimensions (for the token estimate), a short label, and a small preview texture for the composer
    /// thumbnail. Produced by <see cref="AssistantImageCapture"/> and converted to an <see cref="LlmContentPart"/>
    /// at send time.
    /// </summary>
    public sealed class AssistantImageAttachment
    {
        /// <summary>Base64-encoded image bytes (no <c>data:</c> prefix).</summary>
        public string Base64 { get; }
        /// <summary>Media type, e.g. <c>image/png</c> or <c>image/jpeg</c>.</summary>
        public string MediaType { get; }
        /// <summary>Encoded image width in pixels.</summary>
        public int Width { get; }
        /// <summary>Encoded image height in pixels.</summary>
        public int Height { get; }
        /// <summary>Short human label for the thumbnail (e.g. "Scene View", a file name).</summary>
        public string Label { get; }
        /// <summary>Small preview texture for the composer thumbnail; may be <c>null</c>.</summary>
        public Texture2D Preview { get; }

        /// <summary>Creates a staged attachment.</summary>
        public AssistantImageAttachment(string base64, string mediaType, int width, int height, string label, Texture2D preview)
        {
            Base64 = base64 ?? string.Empty;
            MediaType = string.IsNullOrEmpty(mediaType) ? "image/png" : mediaType;
            Width = width;
            Height = height;
            Label = string.IsNullOrEmpty(label) ? "image" : label;
            Preview = preview;
        }

        /// <summary>The provider-neutral content part this attachment sends as.</summary>
        public LlmContentPart ToContentPart() => LlmContentPart.FromImage(Base64, MediaType, Width, Height);
    }

    /// <summary>
    /// Editor-only helpers that turn live editor views, image files, and texture assets into an
    /// <see cref="AssistantImageAttachment"/> for the assistant's multimodal input (Sprint 73). Every capture
    /// downscales to <see cref="MaxDimension"/> and re-encodes to PNG so an oversized source can't bloat the
    /// request or the persisted session. All entry points are exception-safe: a failure returns <c>false</c>
    /// with a reason rather than throwing into a UI callback.
    /// </summary>
    public static class AssistantImageCapture
    {
        /// <summary>Longest edge (px) an attachment is downscaled to — matches vendor guidance for image input.</summary>
        public const int MaxDimension = 1568;

        /// <summary>Captures the last active Scene view camera as a staged attachment.</summary>
        public static bool TryCaptureSceneView(out AssistantImageAttachment attachment, out string error)
        {
            attachment = null;
            error = null;
            var view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null)
            {
                error = "No active Scene view to capture. Open a Scene view and try again.";
                return false;
            }
            return TryCaptureCamera(view.camera, view.position.width, view.position.height, "Scene View", out attachment, out error);
        }

        /// <summary>Captures the main game camera as a staged attachment.</summary>
        public static bool TryCaptureGameView(out AssistantImageAttachment attachment, out string error)
        {
            attachment = null;
            error = null;
            var cam = Camera.main;
            if (cam == null)
            {
                // Fall back to any enabled camera in the active scene.
                foreach (var c in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                    if (c != null && c.isActiveAndEnabled) { cam = c; break; }
            }
            if (cam == null)
            {
                error = "No active camera to capture. Add a camera (or tag one MainCamera) and try again.";
                return false;
            }
            return TryCaptureCamera(cam, cam.pixelWidth, cam.pixelHeight, "Game View", out attachment, out error);
        }

        /// <summary>Loads an image file (PNG/JPEG) from disk into a staged attachment.</summary>
        public static bool TryFromFile(string path, out AssistantImageAttachment attachment, out string error)
        {
            attachment = null;
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    error = "File not found.";
                    return false;
                }
                var bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    error = "Unsupported or corrupt image file.";
                    return false;
                }
                var ok = TryFromTexture(tex, Path.GetFileName(path), out attachment, out error);
                UnityEngine.Object.DestroyImmediate(tex);
                return ok;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Turns an arbitrary <see cref="Texture"/> (a texture asset, render result, or loaded file) into a
        /// staged attachment, downscaling to <see cref="MaxDimension"/> and encoding to PNG. Works with
        /// non-readable textures by blitting through a <see cref="RenderTexture"/>.
        /// </summary>
        public static bool TryFromTexture(Texture source, string label, out AssistantImageAttachment attachment, out string error)
        {
            attachment = null;
            error = null;
            if (source == null)
            {
                error = "No texture to attach.";
                return false;
            }
            try
            {
                var (w, h) = ScaledSize(source.width, source.height);
                var readable = BlitToReadable(source, w, h);
                var bytes = ImageConversion.EncodeToPNG(readable);
                UnityEngine.Object.DestroyImmediate(readable);
                if (bytes == null || bytes.Length == 0)
                {
                    error = "Failed to encode the image.";
                    return false;
                }
                var preview = MakePreview(bytes);
                attachment = new AssistantImageAttachment(Convert.ToBase64String(bytes), "image/png", w, h, label, preview);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryCaptureCamera(Camera cam, float viewW, float viewH, string label,
            out AssistantImageAttachment attachment, out string error)
        {
            attachment = null;
            error = null;
            try
            {
                var w = Mathf.Clamp(Mathf.RoundToInt(viewW), 16, 4096);
                var h = Mathf.Clamp(Mathf.RoundToInt(viewH), 16, 4096);
                var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
                var prevTarget = cam.targetTexture;
                var prevActive = RenderTexture.active;
                try
                {
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var full = new Texture2D(w, h, TextureFormat.RGB24, false);
                    full.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    full.Apply();
                    var ok = TryFromTexture(full, label, out attachment, out error);
                    UnityEngine.Object.DestroyImmediate(full);
                    return ok;
                }
                finally
                {
                    cam.targetTexture = prevTarget;
                    RenderTexture.active = prevActive;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Computes the downscaled dimensions preserving aspect, capping the longest edge.</summary>
        internal static (int width, int height) ScaledSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return (Mathf.Max(1, width), Mathf.Max(1, height));
            var longest = Mathf.Max(width, height);
            if (longest <= MaxDimension) return (width, height);
            var scale = (float)MaxDimension / longest;
            return (Mathf.Max(1, Mathf.RoundToInt(width * scale)), Mathf.Max(1, Mathf.RoundToInt(height * scale)));
        }

        private static Texture2D BlitToReadable(Texture source, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply();
                return readable;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static Texture2D MakePreview(byte[] pngBytes)
        {
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                return ImageConversion.LoadImage(tex, pngBytes) ? tex : null;
            }
            catch { return null; }
        }
    }
}
