using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;
using UnityEngine;

namespace PummelPartyMcp.Tools
{
    /// <summary>screenshot: optionally moves the editor camera first (camera parameter, view only); captures the back buffer at end of frame (after the editor's ImGui pass), PNG, base64.</summary>
    internal static class ScreenshotTool
    {
        public static void Register(ToolRegistry registry, MonoBehaviour host)
        {
            registry.Register(ToolDefinition.Read("screenshot", ctx =>
            {
                int maxWidth = ctx.GetInt("maxWidth", 1280, 64, 7680);
                CameraRequest camera = CameraRules.Parse(ctx.Params["camera"]);
                bool moved = false;
                try
                {
                return ctx.OnMainThreadDeferred(completion =>
                {
                    JObject applied = null;
                    if (camera != null)
                    {
                        // view change only (not a write tool): validated first, then applied once the caller can no longer cancel
                        applied = EditorCameraAccess.Apply(camera, completion.TryCommit);
                        if (applied == null)
                        {
                            return; // the caller gave up before anything changed
                        }
                        moved = true;
                    }
                    host.StartCoroutine(Capture(maxWidth, completion, applied));
                });
                }
                catch (RpcException e) when (moved && e.Code == ErrorCodes.Timeout)
                {
                    // the dispatcher's text is about edits ("check get_object"); this call only moved the view
                    throw new RpcException(ErrorCodes.Timeout, "the camera was moved but the capture did not finish (the game's frames stalled?); call screenshot again");
                }
            }));
        }

        private static IEnumerator Capture(int maxWidth, IWorkCompletion completion, JObject appliedCamera)
        {
            if (appliedCamera != null)
            {
                // two rendered frames: the editor camera snaps on its next Update and the scene view renders after that
                yield return null;
                yield return null;
            }
            yield return new WaitForEndOfFrame();
            if (completion.IsCancelled)
            {
                yield break;
            }
            try
            {
                JObject result = CaptureNow(maxWidth);
                if (appliedCamera != null)
                {
                    result["camera"] = appliedCamera;
                }
                completion.SetResult(result);
            }
            catch (Exception e)
            {
                completion.SetException(e);
            }
        }

        private static JObject CaptureNow(int maxWidth)
        {
            Texture2D full = null;
            Texture2D scaled = null;
            RenderTexture rt = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                full = ScreenCapture.CaptureScreenshotAsTexture();
                if (full == null)
                {
                    throw RpcException.Internal("ScreenCapture returned no texture");
                }
                Texture2D output = full;
                if (full.width > maxWidth)
                {
                    int w = maxWidth;
                    int h = Math.Max(1, (int)Math.Round(full.height * (double)maxWidth / full.width));
                    rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(full, rt);
                    RenderTexture.active = rt;
                    scaled = new Texture2D(w, h, TextureFormat.RGB24, false);
                    scaled.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                    scaled.Apply(false);
                    output = scaled;
                }
                byte[] png = output.EncodeToPNG();
                return new JObject
                {
                    ["pngBase64"] = Convert.ToBase64String(png),
                    ["width"] = output.width,
                    ["height"] = output.height,
                };
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null)
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
                if (scaled != null)
                {
                    UnityEngine.Object.Destroy(scaled);
                }
                if (full != null)
                {
                    UnityEngine.Object.Destroy(full);
                }
            }
        }
    }
}
