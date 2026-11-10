using System;

namespace PummelBridge.Core
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
    }

    /// <summary>Thrown by tools to return a specific error code to the caller.</summary>
    public sealed class RpcException : Exception
    {
        public string Code { get; }

        public RpcException(string code, string message) : base(message)
        {
            Code = code;
        }

        public static RpcException InvalidParams(string message) => new RpcException(ErrorCodes.InvalidParams, message);
        public static RpcException Internal(string message) => new RpcException(ErrorCodes.Internal, message);
        public static RpcException NotFound(string message) => new RpcException(ErrorCodes.NotFound, message);
    }

    /// <summary>Logging abstraction so the core has no BepInEx/Unity dependency.</summary>
    public interface IBridgeLog
    {
        void Debug(string message);
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    public sealed class NullBridgeLog : IBridgeLog
    {
        public static readonly NullBridgeLog Instance = new NullBridgeLog();
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }
}
