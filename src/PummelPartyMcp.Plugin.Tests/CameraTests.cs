using System;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>screenshot camera parameter: validation and look-at maths (Core/CameraRules.cs).</summary>
    internal static class CameraTests
    {
        private static string CodeOf(Action a)
        {
            try
            {
                a();
                return null;
            }
            catch (RpcException e)
            {
                return e.Code;
            }
        }

        private static bool Bad(string json) => CodeOf(() => CameraRules.Parse(JToken.Parse(json))) == ErrorCodes.InvalidParams;

        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

        public static void Run(Action<string, bool> check)
        {
            check("camera: absent is null", CameraRules.Parse(null) == null && CameraRules.Parse(JValue.CreateNull()) == null);

            CameraRequest la = CameraRules.Parse(JToken.Parse("{\"position\":{\"x\":0,\"y\":10,\"z\":0},\"lookAt\":{\"x\":0,\"y\":0,\"z\":0}}"));
            CameraRules.Resolve(la, out double p, out double y);
            check("camera: straight down is pitch 90, yaw 0, no NaN", Near(p, 90) && Near(y, 0));
            CameraRules.Aim(new CameraVec(0, 0, 0), new CameraVec(0, 5, 0), out p, out y);
            check("camera: straight up is pitch -90", Near(p, -90) && Near(y, 0));
            CameraRules.Aim(new CameraVec(0, 0, 0), new CameraVec(0, 0, 5), out p, out y);
            check("camera: +z is pitch 0 yaw 0", Near(p, 0) && Near(y, 0));
            CameraRules.Aim(new CameraVec(0, 0, 0), new CameraVec(5, 0, 0), out p, out y);
            check("camera: +x is yaw 90", Near(p, 0) && Near(y, 90));
            CameraRules.Aim(new CameraVec(0, 0, 0), new CameraVec(-5, 0, 0), out p, out y);
            check("camera: -x is yaw 270 (wrapped to 0..360)", Near(y, 270));
            CameraRules.Aim(new CameraVec(1, 1, 1), new CameraVec(2, 0, 2), out p, out y);
            check("camera: diagonal down is pitch 35.264, yaw 45", Near(p, Math.Atan(1 / Math.Sqrt(2)) * 180 / Math.PI) && Near(y, 45));
            check("camera: same point as lookAt is refused", Bad("{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"lookAt\":{\"x\":1,\"y\":2,\"z\":3}}"));

            CameraRequest rot = CameraRules.Parse(JToken.Parse("{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"rotation\":{\"x\":30,\"y\":-90}}"));
            CameraRules.Resolve(rot, out p, out y);
            check("camera: rotation keeps pitch, wraps yaw", Near(p, 30) && Near(y, 270) && rot.Position.Value.Z == 3);
            CameraRequest clamp = CameraRules.Parse(JToken.Parse("{\"position\":{\"x\":0,\"y\":0,\"z\":0},\"rotation\":{\"x\":120,\"y\":720}}"));
            check("camera: pitch clamps to 90, yaw 720 -> 0", Near(clamp.Pitch.Value, 90) && Near(clamp.Yaw.Value, 0));

            CameraRequest f = CameraRules.Parse(JToken.Parse("{\"focus\":\" abc \"}"));
            check("camera: focus alone", f.Focus == "abc" && !f.HasRotation && !f.Position.HasValue);
            CameraRequest fr = CameraRules.Parse(JToken.Parse("{\"focus\":\"abc\",\"rotation\":{\"x\":45,\"y\":10}}"));
            check("camera: focus with rotation", fr.Focus == "abc" && fr.HasRotation && Near(fr.Pitch.Value, 45) && Near(fr.Yaw.Value, 10));

            check("camera: focus with position refused", Bad("{\"focus\":\"a\",\"position\":{\"x\":0,\"y\":0,\"z\":0}}"));
            check("camera: focus with lookAt refused", Bad("{\"focus\":\"a\",\"lookAt\":{\"x\":0,\"y\":0,\"z\":0}}"));
            check("camera: focus must be a string", Bad("{\"focus\":5}"));
            check("camera: empty focus refused", Bad("{\"focus\":\" \"}"));
            check("camera: position alone refused", Bad("{\"position\":{\"x\":0,\"y\":0,\"z\":0}}"));
            check("camera: lookAt and rotation together refused", Bad("{\"position\":{\"x\":0,\"y\":0,\"z\":0},\"lookAt\":{\"x\":1,\"y\":0,\"z\":0},\"rotation\":{\"x\":0,\"y\":0}}"));
            check("camera: lookAt without position refused", Bad("{\"lookAt\":{\"x\":1,\"y\":0,\"z\":0}}"));
            check("camera: rotation without position refused", Bad("{\"rotation\":{\"x\":1,\"y\":0}}"));
            check("camera: empty object refused", Bad("{}"));
            check("camera: not an object refused", Bad("\"up\""));
            check("camera: unknown key refused", Bad("{\"position\":{\"x\":0,\"y\":0,\"z\":0},\"rotation\":{\"x\":1,\"y\":0},\"zoom\":2}"));
            check("camera: missing axis refused", Bad("{\"position\":{\"x\":0,\"y\":0},\"rotation\":{\"x\":1,\"y\":0}}"));
            check("camera: non-number refused", Bad("{\"position\":{\"x\":\"0\",\"y\":0,\"z\":0},\"rotation\":{\"x\":1,\"y\":0}}"));
            check("camera: roll key refused", Bad("{\"position\":{\"x\":0,\"y\":0,\"z\":0},\"rotation\":{\"x\":1,\"y\":0,\"z\":3}}"));
            check("camera: huge coordinate refused", Bad("{\"position\":{\"x\":1e12,\"y\":0,\"z\":0},\"rotation\":{\"x\":1,\"y\":0}}"));
            // round trip: rebuild the forward vector from the pitch/yaw Aim returns
            Random rnd = new Random(12345);
            bool roundTrip = true;
            for (int i = 0; i < 2000; i++)
            {
                double dx = rnd.NextDouble() * 20 - 10, dy = rnd.NextDouble() * 20 - 10, dz = rnd.NextDouble() * 20 - 10;
                if (i % 10 == 0)
                {
                    dx = 0; // also exercise the vertical axis
                    dz = 0;
                }
                double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len < 1e-6)
                {
                    continue;
                }
                CameraRules.Aim(new CameraVec(0, 0, 0), new CameraVec(dx, dy, dz), out double ap, out double ay);
                double rp = ap * Math.PI / 180, ry = ay * Math.PI / 180;
                double fx = Math.Cos(rp) * Math.Sin(ry), fy = -Math.Sin(rp), fz = Math.Cos(rp) * Math.Cos(ry);
                bool range = ay >= 0 && ay < 360 && ap >= -90 && ap <= 90 && !double.IsNaN(ap) && !double.IsNaN(ay);
                if (!range || Math.Abs(fx - dx / len) > 1e-9 || Math.Abs(fy - dy / len) > 1e-9 || Math.Abs(fz - dz / len) > 1e-9)
                {
                    roundTrip = false;
                    break;
                }
            }
            check("camera: Aim round trip over 2000 random directions (all quadrants and the vertical)", roundTrip);
            check("camera: NormalizeYaw(-1e-15) is 0", CameraRules.NormalizeYaw(-1e-15) == 0.0);
            check("camera: NormalizeYaw(-0.0) is +0", CameraRules.NormalizeYaw(-0.0) == 0.0 && !double.IsNegative(CameraRules.NormalizeYaw(-0.0)));
            check("camera: NormalizeYaw(360) is 0 and (-360) is 0", CameraRules.NormalizeYaw(360) == 0.0 && CameraRules.NormalizeYaw(-360) == 0.0);
            check("camera: NormalizeYaw(-90) is 270", Near(CameraRules.NormalizeYaw(-90), 270));

            check("camera: active object has no focus refusal", CameraRules.FocusRefusal(true, false, false) == null && CameraRules.FocusRefusal(true, true, true) == null);
            check("camera: inactive prefab object, no prefab open -> open_prefab", (CameraRules.FocusRefusal(false, true, false) ?? "").Contains("open_prefab first"));
            check("camera: inactive scene object while a prefab is open -> close_prefab", (CameraRules.FocusRefusal(false, false, true) ?? "").Contains("close_prefab first"));
            check("camera: inactive scene object, no prefab -> inactive", (CameraRules.FocusRefusal(false, false, false) ?? "").Contains("inactive"));
            check("camera: inactive object of another prefab while one is open is refused", CameraRules.FocusRefusal(false, true, true) != null);
            check("camera: rotation needs both angles", Bad("{\"position\":{\"x\":0,\"y\":0,\"z\":0},\"rotation\":{\"x\":1}}"));
        }
    }
}
