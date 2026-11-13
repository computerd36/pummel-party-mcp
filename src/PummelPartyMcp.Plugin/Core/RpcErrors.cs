using System;

namespace PummelPartyMcp.Core
{
    /// <summary>In-band error codes returned as {"ok":false,"error":{"code":...}} (HTTP 200).</summary>
    public static class ErrorCodes
    {
        public const string UnknownMethod = "UNKNOWN_METHOD";
        public const string InvalidParams = "INVALID_PARAMS";
        public const string EditorNotOpen = "EDITOR_NOT_OPEN";
        public const string NoModOpen = "NO_MOD_OPEN";
        public const string BusyLoading = "BUSY_LOADING";
        public const string NotFound = "NOT_FOUND";
        public const string Timeout = "TIMEOUT";
        public const string Internal = "INTERNAL";

        // write tools (api v2)
        public const string WritesDisabled = "WRITES_DISABLED";
        public const string PublishBlockIncomplete = "PUBLISH_BLOCK_INCOMPLETE";
        public const string GuardNotTestMod = "GUARD_NOT_TEST_MOD";
        public const string InvalidState = "INVALID_STATE";
        public const string AlreadyExists = "ALREADY_EXISTS";
        public const string GameVersionUntested = "GAME_VERSION_UNTESTED";

        // asset tools (plugin 0.8.0)
        public const string AssetInUse = "ASSET_IN_USE";
        public const string ImportFailed = "IMPORT_FAILED";

        /// <summary>A path the operation would write or delete runs through a junction/symlink/mount point or is a hard-linked file (SandboxPath).</summary>
        public const string LinkRefused = "LINK_REFUSED";
    }

    /// <summary>Thrown by tools to return a specific error code to the caller.</summary>
    public sealed class RpcException : Exception
    {
        public string Code { get; }

        /// <summary>
        /// Optional structured detail, returned as <c>error.data</c> (a Newtonsoft JToken or any JSON-serialisable object).
        /// Typed as object so this file stays free of the JSON library (it is also compiled into the patch-test project).
        /// </summary>
        public object ErrorData { get; }

        public RpcException(string code, string message) : base(message)
        {
            Code = code;
        }

        public RpcException(string code, string message, object errorData) : base(message)
        {
            Code = code;
            ErrorData = errorData;
        }

        public static RpcException InvalidParams(string message) => new RpcException(ErrorCodes.InvalidParams, message);
        public static RpcException Internal(string message) => new RpcException(ErrorCodes.Internal, message);
        public static RpcException NotFound(string message) => new RpcException(ErrorCodes.NotFound, message);
    }

    /// <summary>Logging abstraction so the core has no BepInEx/Unity dependency.</summary>
    public interface IPluginLog
    {
        void Debug(string message);
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    public sealed class NullPluginLog : IPluginLog
    {
        public static readonly NullPluginLog Instance = new NullPluginLog();
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }
}
