using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    /// <summary>A point or direction in world space (Unity-free).</summary>
    public readonly struct CameraVec
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public CameraVec(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>The validated 'camera' parameter of screenshot.</summary>
    public sealed class CameraRequest
    {
        /// <summary>Camera position; null when only 'focus' (and maybe 'rotation') was given.</summary>
        public CameraVec? Position;

        /// <summary>Point to aim at (position + lookAt form).</summary>
        public CameraVec? LookAt;

        /// <summary>Pitch / yaw in degrees (rotation form, or the direction chosen before a focus). Both null or both set.</summary>
        public double? Pitch;
        public double? Yaw;

        /// <summary>Object guid (normalised by the game side) to frame like the editor's F key.</summary>
        public string Focus;

        public bool HasRotation => Pitch.HasValue && Yaw.HasValue;
    }

    /// <summary>Parameter validation and look-at maths for the editor camera of screenshot. Unity-free (unit tested).</summary>
    public static class CameraRules
    {
        public const double MaxCoordinate = 1000000.0;
        public const double MaxPitch = 90.0;

        /// <summary>Parses the optional camera object. Null when the parameter is absent. Throws INVALID_PARAMS.</summary>
        public static CameraRequest Parse(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }
            if (!(token is JObject obj))
            {
                throw RpcException.InvalidParams("'camera' must be an object: {position, lookAt}, {position, rotation} or {focus}");
            }
            foreach (JProperty p in obj.Properties())
            {
                if (p.Name != "position" && p.Name != "lookAt" && p.Name != "rotation" && p.Name != "focus")
                {
                    throw RpcException.InvalidParams($"'camera.{p.Name}' is not a camera field (allowed: position, lookAt, rotation, focus)");
                }
            }
            bool hasPosition = Present(obj, "position");
            bool hasLookAt = Present(obj, "lookAt");
            bool hasRotation = Present(obj, "rotation");
            bool hasFocus = Present(obj, "focus");

            CameraRequest r = new CameraRequest();
            if (hasFocus)
            {
                if (hasPosition || hasLookAt)
                {
                    throw RpcException.InvalidParams("'camera.focus' cannot be combined with 'position' or 'lookAt'; use focus alone or focus with 'rotation'");
                }
                JToken f = obj["focus"];
                if (f.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)f))
                {
                    throw RpcException.InvalidParams("'camera.focus' must be an object guid string");
                }
                r.Focus = ((string)f).Trim();
                if (hasRotation)
                {
                    ParseRotation(obj["rotation"], r);
                }
                return r;
            }
            if (!hasPosition)
            {
                throw RpcException.InvalidParams("'camera' needs 'position' (with 'lookAt' or 'rotation'), or 'focus'");
            }
            if (hasLookAt == hasRotation)
            {
                throw RpcException.InvalidParams(hasLookAt
                    ? "'camera' takes either 'lookAt' or 'rotation' with 'position', not both"
                    : "'camera.position' needs exactly one of 'lookAt' or 'rotation'");
            }
            r.Position = ParseVec(obj["position"], "camera.position");
            if (hasLookAt)
            {
                r.LookAt = ParseVec(obj["lookAt"], "camera.lookAt");
                CameraVec p = r.Position.Value;
                CameraVec l = r.LookAt.Value;
                if (p.X == l.X && p.Y == l.Y && p.Z == l.Z)
                {
                    throw RpcException.InvalidParams("'camera.position' and 'camera.lookAt' are the same point; there is no direction to look in");
                }
            }
            else
            {
                ParseRotation(obj["rotation"], r);
            }
            return r;
        }

        private static bool Present(JObject obj, string name)
        {
            JToken t = obj[name];
            return t != null && t.Type != JTokenType.Null && t.Type != JTokenType.Undefined;
        }

        private static double Number(JToken t, string path)
        {
            if (t == null || (t.Type != JTokenType.Integer && t.Type != JTokenType.Float))
            {
                throw RpcException.InvalidParams($"'{path}' must be a number");
            }
            double d = (double)t;
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                throw RpcException.InvalidParams($"'{path}' must be a finite number");
            }
            return d;
        }

        private static void RequireKeys(JObject o, string path, params string[] keys)
        {
            foreach (JProperty p in o.Properties())
            {
                if (Array.IndexOf(keys, p.Name) < 0)
                {
                    throw RpcException.InvalidParams($"'{path}.{p.Name}' is not allowed (expected {string.Join(", ", keys)})");
                }
            }
        }

        private static CameraVec ParseVec(JToken t, string path)
        {
            if (!(t is JObject o))
            {
                throw RpcException.InvalidParams($"'{path}' must be an object {{x, y, z}}");
            }
            RequireKeys(o, path, "x", "y", "z");
            double x = Number(o["x"], path + ".x");
            double y = Number(o["y"], path + ".y");
            double z = Number(o["z"], path + ".z");
            if (Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate || Math.Abs(z) > MaxCoordinate)
            {
                throw RpcException.InvalidParams($"'{path}' must stay within +-{MaxCoordinate.ToString("0", CultureInfo.InvariantCulture)} on every axis");
            }
            return new CameraVec(x, y, z);
        }

        private static void ParseRotation(JToken t, CameraRequest r)
        {
            if (!(t is JObject o))
            {
                throw RpcException.InvalidParams("'camera.rotation' must be an object {x, y} (pitch and yaw in degrees)");
            }
            RequireKeys(o, "camera.rotation", "x", "y");
            double pitch = Number(o["x"], "camera.rotation.x");
            double yaw = Number(o["y"], "camera.rotation.y");
            if (Math.Abs(pitch) > 100000 || Math.Abs(yaw) > 100000)
            {
                throw RpcException.InvalidParams("'camera.rotation' angles are out of range");
            }
            r.Pitch = ClampPitch(pitch);
            r.Yaw = NormalizeYaw(yaw);
        }

        /// <summary>
        /// Why a focus target cannot be framed (null = it can): an inactive object has no renderers for Focus, so the camera would
        /// frame empty space and still report success. The prefab root is inactive unless a prefab is open, the scene root while one is.
        /// </summary>
        public static string FocusRefusal(bool activeInHierarchy, bool underPrefabsRoot, bool prefabOpen)
        {
            if (activeInHierarchy)
            {
                return null;
            }
            if (underPrefabsRoot)
            {
                return prefabOpen
                    ? "the object belongs to a prefab that is not the open one (or is switched off); close_prefab, then open_prefab that prefab, to look at it"
                    : "the object belongs to a prefab, which is hidden while no prefab is open; open_prefab first, then focus on it";
            }
            return prefabOpen
                ? "the object belongs to the scene, which is hidden while a prefab is open; close_prefab first, then focus on it"
                : "the object is inactive (switched off), so there is nothing to frame; turn it on (set_object active) first";
        }

        /// <summary>Pitch limited to -90..90 (positive looks down, as in the editor camera).</summary>
        public static double ClampPitch(double pitch) => Math.Max(-MaxPitch, Math.Min(MaxPitch, pitch));

        /// <summary>Yaw wrapped into 0..360 (the editor camera keeps it there).</summary>
        public static double NormalizeYaw(double yaw)
        {
            double y = yaw % 360.0;
            if (y < 0)
            {
                y += 360.0;
            }
            return y >= 360.0 ? 0.0 : y + 0.0; // + 0.0 turns -0.0 into 0.0
        }

        /// <summary>
        /// Pitch and yaw (degrees) of the editor camera looking from <paramref name="from"/> to <paramref name="to"/>.
        /// Rotation is Euler(pitch, yaw, 0): forward = (cos p sin y, -sin p, cos p cos y), so positive pitch looks down.
        /// Straight up or down gives pitch +-90 and yaw 0 (no NaN).
        /// </summary>
        public static void Aim(CameraVec from, CameraVec to, out double pitch, out double yaw)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double dz = to.Z - from.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len <= 0 || double.IsNaN(len) || double.IsInfinity(len))
            {
                throw RpcException.InvalidParams("'camera.position' and 'camera.lookAt' are the same point; there is no direction to look in");
            }
            double s = Math.Max(-1.0, Math.Min(1.0, -dy / len));
            pitch = ClampPitch(Math.Asin(s) * 180.0 / Math.PI);
            double horizontal = Math.Sqrt(dx * dx + dz * dz);
            yaw = horizontal < 1e-9 * len ? 0.0 : NormalizeYaw(Math.Atan2(dx, dz) * 180.0 / Math.PI);
        }

        /// <summary>Pitch/yaw to apply for a position request (lookAt or rotation form).</summary>
        public static void Resolve(CameraRequest r, out double pitch, out double yaw)
        {
            if (r.LookAt.HasValue)
            {
                Aim(r.Position.Value, r.LookAt.Value, out pitch, out yaw);
                return;
            }
            pitch = r.Pitch.Value;
            yaw = r.Yaw.Value;
        }
    }
}
