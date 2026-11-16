using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;
using UnityEngine;

namespace PummelPartyMcp.Tools
{
    /// <summary>
    /// screenshot: optionally moves the editor camera first (camera parameter, view only); captures the back buffer at end of frame
    /// (after the editor's ImGui pass), PNG, base64. Captures run one at a time (<see cref="CaptureQueue"/>): a call's camera is
    /// applied only when its turn comes, so a concurrent call can never move the camera between another call's move and capture.
    /// </summary>
    internal static class ScreenshotTool
    {
        private sealed class ShotRequest
        {
            public int MaxWidth;
            public CameraRequest Camera;
            public volatile bool Moved;
        }

        private static bool s_watching;

        public static void Register(ToolRegistry registry, MonoBehaviour host, FramePoller poller, IPluginLog log)
        {
            CaptureQueue queue = null;
            queue = new CaptureQueue(e => StartCapture(host, queue, e), log);
            registry.Register(ToolDefinition.Read("screenshot", ctx =>
            {
                ShotRequest req = new ShotRequest
                {
                    MaxWidth = ctx.GetInt("maxWidth", 1280, 64, 7680),
                    Camera = CameraRules.Parse(ctx.Params["camera"]),
                };
                try
                {
                    return ctx.OnMainThreadDeferred(completion =>
                    {
                        queue.Enqueue(new CaptureQueue.Entry(completion, req));
                        Watch(queue, poller);
                    });
                }
                catch (RpcException e) when (req.Moved && e.Code == ErrorCodes.Timeout)
                {
                    // the dispatcher's text is about edits ("check get_object"); this call only moved the view
                    throw new RpcException(ErrorCodes.Timeout, "the camera was moved but the capture did not finish (the game's frames stalled?); call screenshot again");
                }
            }));
        }

        /// <summary>Per-frame pump while captures are queued or running, so a slot whose coroutine never finishes is taken back in time.</summary>
        private static void Watch(CaptureQueue queue, FramePoller poller)
        {
            if (s_watching || queue.Idle)
            {
                return;
            }
            s_watching = true;
            poller.Add(() =>
            {
                bool done = true;
                try
                {
                    queue.FrameTick();
                    done = queue.Idle;
                    return done;
                }
                finally
                {
                    if (done)
                    {
                        s_watching = false;
                    }
                }
            });
        }

        /// <summary>The queue's turn for one call: apply its camera (if any, through the commit point), then start the capture coroutine.</summary>
        private static bool StartCapture(MonoBehaviour host, CaptureQueue queue, CaptureQueue.Entry entry)
        {
            ShotRequest req = (ShotRequest)entry.State;
            JObject applied = null;
            if (req.Camera != null)
            {
                // view change only (not a write tool): validated first, then applied once the caller can no longer cancel
                applied = EditorCameraAccess.Apply(req.Camera, entry.Completion.TryCommit);
                if (applied == null)
                {
                    return false; // the caller gave up before anything changed
                }
                req.Moved = true;
            }
            host.StartCoroutine(Capture(req.MaxWidth, entry.Completion, applied, () => queue.Release(entry)));
            return true;
        }

        private static IEnumerator Capture(int maxWidth, IWorkCompletion completion, JObject appliedCamera, Action release)
        {
            try
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
            finally
            {
                release(); // frees the capture slot and starts the next queued call
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
