using System;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using UnityEngine;

namespace PummelPartyMcp.Game
{
    /// <summary>
    /// screenshot's camera parameter: moves the Mod Editor's scene-view camera (view only: nothing in the mod changes, no undo step,
    /// no dirty flag). Main thread only. The EditorCamera is reached as EditorSystem.Instance.SceneViewWidget.CurEditorCamera
    /// (public members). EditorCamera.Update rebuilds the rotation from its private pitch/yaw every frame and SmoothDamps the
    /// position toward the private m_targetPosition, so both the public setters and the transform are written, and the private
    /// m_targetVelocity is zeroed so the view does not drift.
    /// </summary>
    internal static class EditorCameraAccess
    {
        /// <summary>
        /// Validates, then (once <paramref name="commit"/> returns true) applies the request. Returns the applied
        /// {position, rotation}, or null when commit refused (nothing changed). Throws before any change on every error.
        /// </summary>
        public static JObject Apply(CameraRequest req, Func<bool> commit)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            EditorAccess.RequireLoadedMod(ed);
            if (ed.State != EditorState.Editing)
            {
                throw new RpcException(ErrorCodes.InvalidState,
                    $"a playtest is running (editor state {ed.State}): the scene-view camera is only available in edit mode; omit 'camera' to capture the current view, or stop_playtest first");
            }
            EditorCamera cam = ed.SceneViewWidget?.CurEditorCamera;
            if (cam == null)
            {
                throw RpcException.Internal("the editor's scene view has no EditorCamera");
            }
            var targetPosition = GameReflection.RequireField(typeof(EditorCamera), "m_targetPosition");
            var targetVelocity = GameReflection.RequireField(typeof(EditorCamera), "m_targetVelocity");
            var pitchField = GameReflection.RequireField(typeof(EditorCamera), "m_zRotation"); // the camera's pitch, despite the name
            var yawField = GameReflection.RequireField(typeof(EditorCamera), "m_yRotation");

            GameObject focusObject = null;
            double pitch = 0;
            double yaw = 0;
            if (req.Focus != null)
            {
                string guid = EditorAccess.NormalizeGuid(req.Focus, "camera.focus");
                ModTransform mt = EditorAccess.FindByGuid(ed, guid, out Transform foundRoot);
                if (mt == null)
                {
                    throw RpcException.NotFound($"no object with guid {guid} in the open mod (get_hierarchy lists them)");
                }
                focusObject = mt.gameObject;
                string refusal = CameraRules.FocusRefusal(focusObject.activeInHierarchy, foundRoot == ed.m_prefabsRoot, ed.CurrentPrefab != null);
                if (refusal != null)
                {
                    throw new RpcException(ErrorCodes.InvalidState, refusal);
                }
                if (req.HasRotation)
                {
                    pitch = req.Pitch.Value;
                    yaw = req.Yaw.Value;
                }
            }
            else
            {
                CameraRules.Resolve(req, out pitch, out yaw);
            }

            if (!commit())
            {
                return null;
            }

            Transform t = cam.transform;
            double reportPitch;
            double reportYaw;
            if (req.Focus == null || req.HasRotation)
            {
                reportPitch = pitch;
                reportYaw = yaw;
                cam.SetRotation(new Vector3((float)pitch, (float)yaw, 0f));
                t.rotation = Quaternion.Euler((float)pitch, (float)yaw, 0f);
            }
            else
            {
                // focus without rotation keeps the camera's own direction: report its pitch/yaw fields (not the degenerate Euler angles)
                reportPitch = pitchField.GetValue(cam) is float pf ? pf : 0f;
                reportYaw = yawField.GetValue(cam) is float yf ? CameraRules.NormalizeYaw(yf) : 0f;
            }
            if (focusObject == null)
            {
                Vector3 p = new Vector3((float)req.Position.Value.X, (float)req.Position.Value.Y, (float)req.Position.Value.Z);
                cam.SetTargetPosition(p);
                t.position = p;
            }
            else
            {
                Camera unityCamera = cam.m_camera != null ? cam.m_camera : cam.GetComponent<Camera>();
                if (focusObject.GetComponentsInChildren<Renderer>().Length > 0 && unityCamera != null)
                {
                    cam.Focus(focusObject, unityCamera);
                }
                else
                {
                    // Focus would take the selection pivot for an object without renderers; frame the object's own position instead
                    // (the same 2 unit distance Focus uses).
                    cam.SetTargetPosition(focusObject.transform.position - 2f * t.forward);
                }
                t.position = (Vector3)targetPosition.GetValue(cam);
            }
            targetVelocity.SetValue(cam, Vector3.zero);

            Vector3 pos = t.position;
            return new JObject
            {
                ["position"] = new JObject { ["x"] = Math.Round(pos.x, 3), ["y"] = Math.Round(pos.y, 3), ["z"] = Math.Round(pos.z, 3) },
                ["rotation"] = new JObject { ["x"] = Math.Round(reportPitch, 3), ["y"] = Math.Round(reportYaw, 3) },
            };
        }
    }
}
