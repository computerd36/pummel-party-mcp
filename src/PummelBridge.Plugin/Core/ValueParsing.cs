using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace PummelBridge.Core
{
    /// <summary>Value categories a reflected property can be written with (game-independent description).</summary>
    public enum ValueKind
    {
        Unsupported,
        Bool,
        Integer,
        Float,
        Double,
        String,
        Enum,
        Vector2,
        Vector3,
        Vector4,
        Quaternion,
        Color,
        Color32,
        Rect,
        LayerMask,
        /// <summary>Asset or scene-object reference: null, a guid string, or {"guid": ...}.</summary>
        Reference,
        Array,
        AnimationCurve,
        MinMaxCurve,
        Gradient,
        MinMaxGradient,
    }

    /// <summary>Describes the expected JSON value for a property type. Built by the game layer from System.Type.</summary>
    public sealed class ValueTypeDesc
    {
        public ValueKind Kind;

        /// <summary>Type name used in messages (same as get_object's "type").</summary>
        public string TypeName;

        public decimal IntMin;
        public decimal IntMax;

        public string[] EnumNames;
        public long[] EnumValues;
        public bool IsFlags;

        public ValueTypeDesc Element;

        public string UnsupportedReason;

        public static ValueTypeDesc Simple(ValueKind kind, string typeName) => new ValueTypeDesc { Kind = kind, TypeName = typeName };

        public static ValueTypeDesc Integer(string typeName, decimal min, decimal max) =>
            new ValueTypeDesc { Kind = ValueKind.Integer, TypeName = typeName, IntMin = min, IntMax = max };

        public static ValueTypeDesc Enum(string typeName, string[] names, long[] values, bool flags) =>
            new ValueTypeDesc { Kind = ValueKind.Enum, TypeName = typeName, EnumNames = names, EnumValues = values, IsFlags = flags };

        public static ValueTypeDesc Reference(string typeName) => new ValueTypeDesc { Kind = ValueKind.Reference, TypeName = typeName };

        public static ValueTypeDesc ArrayOf(ValueTypeDesc element) =>
            new ValueTypeDesc { Kind = ValueKind.Array, TypeName = element.TypeName + "[]", Element = element };

        public static ValueTypeDesc Unsupported(string typeName, string reason) =>
            new ValueTypeDesc { Kind = ValueKind.Unsupported, TypeName = typeName, UnsupportedReason = reason };

        /// <summary>Null if writable, else the reason (also checks array elements).</summary>
        public string WhyUnsupported()
        {
            if (Kind == ValueKind.Unsupported)
            {
                return UnsupportedReason ?? $"values of type {TypeName} cannot be set by the bridge";
            }
            return Kind == ValueKind.Array ? Element?.WhyUnsupported() : null;
        }

        /// <summary>Short description of the accepted JSON, for error messages.</summary>
        public string Expectation()
        {
            switch (Kind)
            {
                case ValueKind.Bool: return "a boolean";
                case ValueKind.Integer: return $"an integer between {IntMin.ToString(CultureInfo.InvariantCulture)} and {IntMax.ToString(CultureInfo.InvariantCulture)}";
                case ValueKind.Float:
                case ValueKind.Double: return "a finite number";
                case ValueKind.String: return "a string";
                case ValueKind.Enum: return IsFlags
                    ? $"flag names joined by ',' (one of {string.Join(", ", EnumNames)}) or an integer"
                    : $"one of {string.Join(", ", EnumNames)} (case-insensitive) or its integer value";
                case ValueKind.Vector2: return "{x, y}";
                case ValueKind.Vector3: return "{x, y, z}";
                case ValueKind.Vector4:
                case ValueKind.Quaternion: return "{x, y, z, w}";
                case ValueKind.Color: return "{r, g, b, a?} (floats, a defaults to 1)";
                case ValueKind.Color32: return "{r, g, b, a?} (integers 0-255, a defaults to 255)";
                case ValueKind.Rect: return "{x, y, width, height}";
                case ValueKind.LayerMask: return "an integer bit mask";
                case ValueKind.Reference: return "null, a guid string or {\"guid\": \"...\"}";
                case ValueKind.Array: return "an array of " + (Element?.TypeName ?? "?");
                case ValueKind.AnimationCurve: return "{keys: [{time, value, inTangent?, outTangent?}], preWrapMode?, postWrapMode?}";
                case ValueKind.MinMaxCurve: return "a number, or {mode: Constant|TwoConstants|Curve|TwoCurves, ...} as get_object returns it";
                case ValueKind.Gradient: return "{mode?, colorKeys: [{color, time}], alphaKeys: [{alpha, time}]}";
                case ValueKind.MinMaxGradient: return "a color, or {mode: Color|TwoColors|Gradient|TwoGradients|RandomColor, ...} as get_object returns it";
                default: return "a supported value";
            }
        }
    }

    // ---------------------------------------------------------------- parsed (neutral) values

    public abstract class PValue
    {
    }

    public sealed class PBool : PValue
    {
        public bool Value;
    }

    public sealed class PInteger : PValue
    {
        public decimal Value;
    }

    public sealed class PNumber : PValue
    {
        public double Value;
    }

    public sealed class PString : PValue
    {
        public string Value;
    }

    public sealed class PEnum : PValue
    {
        public long Value;
    }

    /// <summary>Vectors, quaternions, colors (r,g,b,a), rects (x,y,width,height).</summary>
    public sealed class PFloats : PValue
    {
        public float[] Values;
    }

    public sealed class PBytes : PValue
    {
        public byte[] Values;
    }

    public sealed class PRef : PValue
    {
        /// <summary>Normalised lowercase guid, or null for "no reference".</summary>
        public string Guid;
    }

    public sealed class PArray : PValue
    {
        public List<PValue> Items = new List<PValue>();
    }

    public sealed class PKey
    {
        public float Time;
        public float Value;
        public float InTangent;
        public float OutTangent;
    }

    public sealed class PCurve : PValue
    {
        public List<PKey> Keys = new List<PKey>();
        public string PreWrapMode = "ClampForever";
        public string PostWrapMode = "ClampForever";
    }

    public sealed class PMinMaxCurve : PValue
    {
        public string Mode;
        public float Constant;
        public float ConstantMin;
        public float ConstantMax;
        public float CurveMultiplier = 1f;
        public PCurve Curve;
        public PCurve CurveMin;
        public PCurve CurveMax;
    }

    public sealed class PGradient : PValue
    {
        public string Mode = "Blend";
        public List<float[]> ColorKeys = new List<float[]>();  // r,g,b,a,time
        public List<float[]> AlphaKeys = new List<float[]>();  // alpha,time
    }

    public sealed class PMinMaxGradient : PValue
    {
        public string Mode;
        public float[] Color;
        public float[] ColorMin;
        public float[] ColorMax;
        public PGradient Gradient;
        public PGradient GradientMin;
        public PGradient GradientMax;
    }

    /// <summary>
    /// JSON -> neutral value conversion with validation. Accepts exactly the shapes get_object returns (round trip), plus
    /// a few conveniences (arrays for vectors, a bare number for a constant MinMaxCurve, a bare color for MinMaxGradient).
    /// Errors are INVALID_PARAMS naming the property path and the expected type.
    /// </summary>
    public static class ValueParser
    {
        public const int MaxArrayLength = 1024;
        public const int MaxStringLength = 16384;
        public const int MaxCurveKeys = 256;
        public const int MaxGradientKeys = 8; // Unity's limit

        private static readonly string[] s_wrapModes = { "Default", "Once", "Clamp", "Loop", "PingPong", "ClampForever" };
        private static readonly string[] s_curveModes = { "Constant", "TwoConstants", "Curve", "TwoCurves" };
        private static readonly string[] s_gradientModes = { "Blend", "Fixed", "PerceptualBlend" };
        private static readonly string[] s_minMaxGradientModes = { "Color", "TwoColors", "Gradient", "TwoGradients", "RandomColor" };

        public static PValue Parse(JToken token, ValueTypeDesc desc, string path)
        {
            if (desc == null)
            {
                throw RpcException.Internal("no type description for " + path);
            }
            string why = desc.WhyUnsupported();
            if (why != null)
            {
                throw RpcException.InvalidParams($"'{path}': {why}");
            }
            token = token ?? JValue.CreateNull();
            switch (desc.Kind)
            {
                case ValueKind.Bool:
                    if (token.Type != JTokenType.Boolean)
                    {
                        throw Mismatch(path, desc, token);
                    }
                    return new PBool { Value = (bool)token };

                case ValueKind.Integer:
                    return new PInteger { Value = Integer(token, desc, path) };

                case ValueKind.Float:
                    return new PNumber { Value = Number(token, desc, path, single: true) };

                case ValueKind.Double:
                    return new PNumber { Value = Number(token, desc, path, single: false) };

                case ValueKind.String:
                    if (token.Type != JTokenType.String)
                    {
                        throw Mismatch(path, desc, token);
                    }
                    string s = (string)token;
                    if (s.Length > MaxStringLength)
                    {
                        throw RpcException.InvalidParams($"'{path}': string longer than {MaxStringLength} characters");
                    }
                    return new PString { Value = s };

                case ValueKind.Enum:
                    return new PEnum { Value = EnumValue(token, desc, path) };

                case ValueKind.Vector2:
                    return new PFloats { Values = Floats(token, desc, path, new[] { "x", "y" }, null) };
                case ValueKind.Vector3:
                    return new PFloats { Values = Floats(token, desc, path, new[] { "x", "y", "z" }, null) };
                case ValueKind.Vector4:
                case ValueKind.Quaternion:
                    return new PFloats { Values = Floats(token, desc, path, new[] { "x", "y", "z", "w" }, null) };
                case ValueKind.Color:
                    return new PFloats { Values = ColorFloats(token, desc, path) };
                case ValueKind.Color32:
                    return new PBytes { Values = Color32(token, desc, path) };
                case ValueKind.Rect:
                    return new PFloats { Values = Floats(token, desc, path, new[] { "x", "y", "width", "height" }, null) };
                case ValueKind.LayerMask:
                    return new PInteger { Value = Integer(token, ValueTypeDesc.Integer(desc.TypeName, int.MinValue, int.MaxValue), path) };

                case ValueKind.Reference:
                    return Reference(token, desc, path);

                case ValueKind.Array:
                    if (token.Type != JTokenType.Array)
                    {
                        throw Mismatch(path, desc, token);
                    }
                    JArray arr = (JArray)token;
                    if (arr.Count > MaxArrayLength)
                    {
                        throw RpcException.InvalidParams($"'{path}': more than {MaxArrayLength} elements");
                    }
                    PArray result = new PArray();
                    for (int i = 0; i < arr.Count; i++)
                    {
                        if (arr[i] is JObject marker && marker.ContainsKey("truncatedItems"))
                        {
                            throw RpcException.InvalidParams($"'{path}[{i}]': a {{truncatedItems}} marker from get_object cannot be written back; send the full array");
                        }
                        result.Items.Add(Parse(arr[i], desc.Element, $"{path}[{i}]"));
                    }
                    return result;

                case ValueKind.AnimationCurve:
                    return Curve(token, desc, path);

                case ValueKind.MinMaxCurve:
                    return MinMaxCurve(token, desc, path);

                case ValueKind.Gradient:
                    return Gradient(token, desc, path);

                case ValueKind.MinMaxGradient:
                    return MinMaxGradient(token, desc, path);

                default:
                    throw RpcException.InvalidParams($"'{path}': values of type {desc.TypeName} cannot be set by the bridge");
            }
        }

        // ---------------------------------------------------------------- scalars

        private static RpcException Mismatch(string path, ValueTypeDesc desc, JToken token) =>
            RpcException.InvalidParams($"'{path}': expected {desc.TypeName}: {desc.Expectation()} (got {Describe(token)})");

        private static string Describe(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Null: return "null";
                case JTokenType.Integer:
                case JTokenType.Float: return "number " + t.ToString(Newtonsoft.Json.Formatting.None);
                case JTokenType.String:
                    string s = (string)t;
                    return "string \"" + (s.Length > 40 ? s.Substring(0, 40) + "..." : s) + "\"";
                case JTokenType.Boolean: return "boolean";
                case JTokenType.Array: return "array";
                case JTokenType.Object: return "object";
                default: return t.Type.ToString();
            }
        }

        private static decimal Integer(JToken token, ValueTypeDesc desc, string path)
        {
            decimal v;
            if (token.Type == JTokenType.Integer)
            {
                try
                {
                    object raw = ((JValue)token).Value;
                    v = raw is System.Numerics.BigInteger big ? (decimal)big : Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    throw RpcException.InvalidParams($"'{path}': integer out of range for {desc.TypeName}");
                }
            }
            else if (token.Type == JTokenType.Float)
            {
                double d = (double)token;
                if (double.IsNaN(d) || double.IsInfinity(d) || Math.Floor(d) != d || Math.Abs(d) > 9.2e18)
                {
                    throw Mismatch(path, desc, token);
                }
                v = (decimal)d;
            }
            else
            {
                throw Mismatch(path, desc, token);
            }
            if (v < desc.IntMin || v > desc.IntMax)
            {
                throw RpcException.InvalidParams($"'{path}': {v.ToString(CultureInfo.InvariantCulture)} is out of range for {desc.TypeName} ({desc.IntMin.ToString(CultureInfo.InvariantCulture)}..{desc.IntMax.ToString(CultureInfo.InvariantCulture)})");
            }
            return v;
        }

        private static double Number(JToken token, ValueTypeDesc desc, string path, bool single)
        {
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                throw Mismatch(path, desc, token);
            }
            double d;
            try
            {
                d = token.Value<double>();
            }
            catch (Exception)
            {
                throw Mismatch(path, desc, token);
            }
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                throw RpcException.InvalidParams($"'{path}': NaN/Infinity are not accepted");
            }
            if (single && Math.Abs(d) > float.MaxValue)
            {
                throw RpcException.InvalidParams($"'{path}': {d.ToString(CultureInfo.InvariantCulture)} does not fit a 32-bit float");
            }
            return d;
        }

        private static float Float(JToken token, string path)
        {
            return (float)Number(token, ValueTypeDesc.Simple(ValueKind.Float, "Single"), path, single: true);
        }

        private static long EnumValue(JToken token, ValueTypeDesc desc, string path)
        {
            string[] names = desc.EnumNames ?? new string[0];
            long[] values = desc.EnumValues ?? new long[0];
            if (token.Type == JTokenType.String)
            {
                string text = ((string)token).Trim();
                string[] parts = desc.IsFlags ? text.Split(',') : new[] { text };
                long acc = 0;
                foreach (string raw in parts)
                {
                    string part = raw.Trim();
                    int idx = -1;
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (string.Equals(names[i], part, StringComparison.OrdinalIgnoreCase))
                        {
                            idx = i;
                            break;
                        }
                    }
                    if (idx < 0)
                    {
                        throw RpcException.InvalidParams($"'{path}': '{part}' is not a {desc.TypeName} value; expected {desc.Expectation()}");
                    }
                    acc |= values[idx];
                }
                return acc;
            }
            if (token.Type == JTokenType.Integer)
            {
                long n;
                try
                {
                    n = (long)token;
                }
                catch (Exception)
                {
                    throw Mismatch(path, desc, token);
                }
                if (desc.IsFlags)
                {
                    long all = 0;
                    foreach (long v in values)
                    {
                        all |= v;
                    }
                    if ((n & ~all) != 0)
                    {
                        throw RpcException.InvalidParams($"'{path}': {n} contains bits that are not {desc.TypeName} flags");
                    }
                    return n;
                }
                foreach (long v in values)
                {
                    if (v == n)
                    {
                        return n;
                    }
                }
                throw RpcException.InvalidParams($"'{path}': {n} is not a defined {desc.TypeName} value; expected {desc.Expectation()}");
            }
            throw Mismatch(path, desc, token);
        }

        // ---------------------------------------------------------------- composites

        private static float[] Floats(JToken token, ValueTypeDesc desc, string path, string[] keys, float?[] defaults)
        {
            float[] result = new float[keys.Length];
            if (token is JArray arr)
            {
                if (arr.Count != keys.Length)
                {
                    throw RpcException.InvalidParams($"'{path}': expected {desc.TypeName}: {desc.Expectation()} or an array of {keys.Length} numbers");
                }
                for (int i = 0; i < keys.Length; i++)
                {
                    result[i] = Float(arr[i], $"{path}[{i}]");
                }
                return result;
            }
            if (!(token is JObject obj))
            {
                throw Mismatch(path, desc, token);
            }
            CheckKeys(obj, keys, path, desc);
            for (int i = 0; i < keys.Length; i++)
            {
                JToken v = obj[keys[i]];
                if (v == null || v.Type == JTokenType.Null)
                {
                    if (defaults != null && defaults[i].HasValue)
                    {
                        result[i] = defaults[i].Value;
                        continue;
                    }
                    throw RpcException.InvalidParams($"'{path}': missing '{keys[i]}'; expected {desc.TypeName}: {desc.Expectation()}");
                }
                result[i] = Float(v, $"{path}.{keys[i]}");
            }
            return result;
        }

        private static float[] ColorFloats(JToken token, ValueTypeDesc desc, string path) =>
            Floats(token, desc, path, new[] { "r", "g", "b", "a" }, new float?[] { null, null, null, 1f });

        private static byte[] Color32(JToken token, ValueTypeDesc desc, string path)
        {
            if (!(token is JObject obj))
            {
                throw Mismatch(path, desc, token);
            }
            string[] keys = { "r", "g", "b", "a" };
            CheckKeys(obj, keys, path, desc);
            byte[] result = new byte[4];
            ValueTypeDesc b = ValueTypeDesc.Integer("Byte", 0, 255);
            for (int i = 0; i < 4; i++)
            {
                JToken v = obj[keys[i]];
                if (v == null || v.Type == JTokenType.Null)
                {
                    if (i == 3)
                    {
                        result[i] = 255;
                        continue;
                    }
                    throw RpcException.InvalidParams($"'{path}': missing '{keys[i]}'; expected {desc.TypeName}: {desc.Expectation()}");
                }
                result[i] = (byte)Integer(v, b, $"{path}.{keys[i]}");
            }
            return result;
        }

        private static void CheckKeys(JObject obj, string[] allowed, string path, ValueTypeDesc desc)
        {
            foreach (JProperty p in obj.Properties())
            {
                if (Array.IndexOf(allowed, p.Name) < 0)
                {
                    throw RpcException.InvalidParams($"'{path}': unknown key '{p.Name}'; expected {desc.TypeName}: {desc.Expectation()}");
                }
            }
        }

        private static PRef Reference(JToken token, ValueTypeDesc desc, string path)
        {
            if (token.Type == JTokenType.Null)
            {
                return new PRef { Guid = null };
            }
            JToken g = token;
            if (token is JObject obj)
            {
                if (!obj.ContainsKey("guid"))
                {
                    throw RpcException.InvalidParams($"'{path}': a reference object needs a 'guid' key; expected {desc.Expectation()}");
                }
                g = obj["guid"];
                if (g == null || g.Type == JTokenType.Null)
                {
                    if (obj["registered"]?.Type == JTokenType.Boolean && !(bool)obj["registered"])
                    {
                        throw RpcException.InvalidParams($"'{path}': this reference is not registered as a mod asset (guid null in get_object); it cannot be written back");
                    }
                    return new PRef { Guid = null };
                }
            }
            if (g.Type != JTokenType.String)
            {
                throw Mismatch(path, desc, token);
            }
            if (!Guid.TryParse((string)g, out Guid parsed))
            {
                throw RpcException.InvalidParams($"'{path}': '{(string)g}' is not a valid GUID");
            }
            return new PRef { Guid = parsed.ToString() };
        }

        private static string Choice(JToken token, string[] options, string path, string what)
        {
            if (token == null || token.Type != JTokenType.String)
            {
                throw RpcException.InvalidParams($"'{path}': {what} must be one of {string.Join(", ", options)}");
            }
            string s = ((string)token).Trim();
            foreach (string o in options)
            {
                if (string.Equals(o, s, StringComparison.OrdinalIgnoreCase))
                {
                    return o;
                }
            }
            throw RpcException.InvalidParams($"'{path}': '{s}' is not a valid {what}; expected one of {string.Join(", ", options)}");
        }

        private static PCurve Curve(JToken token, ValueTypeDesc desc, string path)
        {
            if (!(token is JObject obj))
            {
                throw Mismatch(path, desc, token);
            }
            CheckKeys(obj, new[] { "keys", "preWrapMode", "postWrapMode" }, path, desc);
            if (!(obj["keys"] is JArray keys))
            {
                throw RpcException.InvalidParams($"'{path}': 'keys' must be an array; expected {desc.Expectation()}");
            }
            if (keys.Count > MaxCurveKeys)
            {
                throw RpcException.InvalidParams($"'{path}': more than {MaxCurveKeys} keys");
            }
            PCurve curve = new PCurve();
            for (int i = 0; i < keys.Count; i++)
            {
                string kp = $"{path}.keys[{i}]";
                if (!(keys[i] is JObject k))
                {
                    throw RpcException.InvalidParams($"'{kp}': expected {{time, value, inTangent?, outTangent?}}");
                }
                CheckKeys(k, new[] { "time", "value", "inTangent", "outTangent" }, kp, desc);
                if (k["time"] == null || k["value"] == null)
                {
                    throw RpcException.InvalidParams($"'{kp}': 'time' and 'value' are required");
                }
                curve.Keys.Add(new PKey
                {
                    Time = Float(k["time"], kp + ".time"),
                    Value = Float(k["value"], kp + ".value"),
                    InTangent = k["inTangent"] == null ? 0f : Float(k["inTangent"], kp + ".inTangent"),
                    OutTangent = k["outTangent"] == null ? 0f : Float(k["outTangent"], kp + ".outTangent"),
                });
            }
            if (obj["preWrapMode"] != null)
            {
                curve.PreWrapMode = Choice(obj["preWrapMode"], s_wrapModes, path + ".preWrapMode", "wrap mode");
            }
            if (obj["postWrapMode"] != null)
            {
                curve.PostWrapMode = Choice(obj["postWrapMode"], s_wrapModes, path + ".postWrapMode", "wrap mode");
            }
            return curve;
        }

        private static PMinMaxCurve MinMaxCurve(JToken token, ValueTypeDesc desc, string path)
        {
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                return new PMinMaxCurve { Mode = "Constant", Constant = Float(token, path) };
            }
            if (!(token is JObject obj))
            {
                throw Mismatch(path, desc, token);
            }
            CheckKeys(obj, new[] { "mode", "constant", "constantMin", "constantMax", "curveMultiplier", "curve", "curveMin", "curveMax" }, path, desc);
            PMinMaxCurve c = new PMinMaxCurve { Mode = Choice(obj["mode"], s_curveModes, path + ".mode", "curve mode") };
            ValueTypeDesc curveDesc = ValueTypeDesc.Simple(ValueKind.AnimationCurve, "AnimationCurve");
            switch (c.Mode)
            {
                case "Constant":
                    c.Constant = Float(Required(obj, "constant", path), path + ".constant");
                    break;
                case "TwoConstants":
                    c.ConstantMin = Float(Required(obj, "constantMin", path), path + ".constantMin");
                    c.ConstantMax = Float(Required(obj, "constantMax", path), path + ".constantMax");
                    break;
                case "Curve":
                    c.CurveMultiplier = obj["curveMultiplier"] == null ? 1f : Float(obj["curveMultiplier"], path + ".curveMultiplier");
                    c.Curve = Curve(Required(obj, "curve", path), curveDesc, path + ".curve");
                    break;
                case "TwoCurves":
                    c.CurveMultiplier = obj["curveMultiplier"] == null ? 1f : Float(obj["curveMultiplier"], path + ".curveMultiplier");
                    c.CurveMin = Curve(Required(obj, "curveMin", path), curveDesc, path + ".curveMin");
                    c.CurveMax = Curve(Required(obj, "curveMax", path), curveDesc, path + ".curveMax");
                    break;
            }
            return c;
        }

        private static PGradient Gradient(JToken token, ValueTypeDesc desc, string path)
        {
            if (!(token is JObject obj))
            {
                throw Mismatch(path, desc, token);
            }
            CheckKeys(obj, new[] { "mode", "colorKeys", "alphaKeys" }, path, desc);
            PGradient g = new PGradient();
            if (obj["mode"] != null)
            {
                g.Mode = Choice(obj["mode"], s_gradientModes, path + ".mode", "gradient mode");
            }
            if (!(obj["colorKeys"] is JArray colors) || !(obj["alphaKeys"] is JArray alphas))
            {
                throw RpcException.InvalidParams($"'{path}': 'colorKeys' and 'alphaKeys' arrays are required; expected {desc.Expectation()}");
            }
            if (colors.Count < 1 || colors.Count > MaxGradientKeys || alphas.Count < 1 || alphas.Count > MaxGradientKeys)
            {
                throw RpcException.InvalidParams($"'{path}': a gradient needs 1..{MaxGradientKeys} color keys and 1..{MaxGradientKeys} alpha keys");
            }
            ValueTypeDesc colorDesc = ValueTypeDesc.Simple(ValueKind.Color, "Color");
            for (int i = 0; i < colors.Count; i++)
            {
                string kp = $"{path}.colorKeys[{i}]";
                if (!(colors[i] is JObject k))
                {
                    throw RpcException.InvalidParams($"'{kp}': expected {{color, time}}");
                }
                CheckKeys(k, new[] { "color", "time" }, kp, desc);
                float[] c = ColorFloats(Required(k, "color", kp), colorDesc, kp + ".color");
                float t = Time01(Required(k, "time", kp), kp + ".time");
                g.ColorKeys.Add(new[] { c[0], c[1], c[2], c[3], t });
            }
            for (int i = 0; i < alphas.Count; i++)
            {
                string kp = $"{path}.alphaKeys[{i}]";
                if (!(alphas[i] is JObject k))
                {
                    throw RpcException.InvalidParams($"'{kp}': expected {{alpha, time}}");
                }
                CheckKeys(k, new[] { "alpha", "time" }, kp, desc);
                g.AlphaKeys.Add(new[] { Float(Required(k, "alpha", kp), kp + ".alpha"), Time01(Required(k, "time", kp), kp + ".time") });
            }
            return g;
        }

        private static PMinMaxGradient MinMaxGradient(JToken token, ValueTypeDesc desc, string path)
        {
            if (!(token is JObject obj))
            {
                throw Mismatch(path, desc, token);
            }
            ValueTypeDesc colorDesc = ValueTypeDesc.Simple(ValueKind.Color, "Color");
            ValueTypeDesc gradDesc = ValueTypeDesc.Simple(ValueKind.Gradient, "Gradient");
            if (obj["mode"] == null && obj.ContainsKey("r"))
            {
                return new PMinMaxGradient { Mode = "Color", Color = ColorFloats(obj, colorDesc, path) };
            }
            CheckKeys(obj, new[] { "mode", "color", "colorMin", "colorMax", "gradient", "gradientMin", "gradientMax" }, path, desc);
            PMinMaxGradient g = new PMinMaxGradient { Mode = Choice(obj["mode"], s_minMaxGradientModes, path + ".mode", "gradient mode") };
            switch (g.Mode)
            {
                case "Color":
                    g.Color = ColorFloats(Required(obj, "color", path), colorDesc, path + ".color");
                    break;
                case "TwoColors":
                    g.ColorMin = ColorFloats(Required(obj, "colorMin", path), colorDesc, path + ".colorMin");
                    g.ColorMax = ColorFloats(Required(obj, "colorMax", path), colorDesc, path + ".colorMax");
                    break;
                case "Gradient":
                case "RandomColor":
                    g.Gradient = Gradient(Required(obj, "gradient", path), gradDesc, path + ".gradient");
                    break;
                case "TwoGradients":
                    g.GradientMin = Gradient(Required(obj, "gradientMin", path), gradDesc, path + ".gradientMin");
                    g.GradientMax = Gradient(Required(obj, "gradientMax", path), gradDesc, path + ".gradientMax");
                    break;
            }
            return g;
        }

        private static JToken Required(JObject obj, string key, string path)
        {
            JToken t = obj[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                throw RpcException.InvalidParams($"'{path}': '{key}' is required");
            }
            return t;
        }

        private static float Time01(JToken token, string path)
        {
            float t = Float(token, path);
            if (t < 0f || t > 1f)
            {
                throw RpcException.InvalidParams($"'{path}': time must be between 0 and 1");
            }
            return t;
        }
    }
}
