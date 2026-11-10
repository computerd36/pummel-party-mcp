namespace PummelBridge
{
    internal static class PluginInfo
    {
        public const string Guid = "com.pummelbridge.plugin";
        public const string Name = "PummelBridge";
        public const string Version = "0.1.0";

        /// <summary>Version of the HTTP/RPC contract (docs/bridge-api.md). Bump on breaking changes.</summary>
        public const int ApiVersion = 1;

        public const string TokenFileName = "PummelBridge.token";
    }
}
