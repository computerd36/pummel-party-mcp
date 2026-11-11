using System;

namespace PummelBridge.Core
{
    /// <summary>
    /// The sandbox rule (CLAUDE.md hard rule 4): bridge write tools only act on mods whose name passes this check.
    /// Hardcoded on purpose: no configurable prefix, no bypass.
    /// </summary>
    public static class TestModName
    {
        /// <summary>Exact, case-sensitive (ordinal) prefix.</summary>
        public const string Prefix = "PB_TEST_";

        public const int MaxLength = 64;

        public static bool IsValid(string name) => Validate(name) == null;

        /// <summary>Null when <paramref name="name"/> is a valid test-mod name, otherwise the reason it is not.</summary>
        public static string Validate(string name)
        {
            if (name == null)
            {
                return "no name";
            }
            if (!name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return $"does not start with '{Prefix}' (case-sensitive)";
            }
            if (name.Length == Prefix.Length)
            {
                return $"needs at least one character after '{Prefix}'";
            }
            if (name.Length > MaxLength)
            {
                return $"is longer than {MaxLength} characters";
            }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == ' ' || c == '-';
                if (!ok)
                {
                    return "may only contain A-Z, a-z, 0-9, '_', '-' and spaces";
                }
            }
            if (name[0] == ' ' || name[name.Length - 1] == ' ')
            {
                return "has a leading or trailing space";
            }
            return null;
        }
    }
}
