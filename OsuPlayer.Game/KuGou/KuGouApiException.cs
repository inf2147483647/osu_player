namespace OsuPlayer.Game.KuGou
{
    /// <summary>Raised when a KuGou gateway response reports an error (status 0 / non-zero error_code).</summary>
    public sealed class KuGouApiException : Exception
    {
        public int ErrorCode { get; }

        public KuGouApiException(string message, int errorCode = 0)
            : base(message)
        {
            ErrorCode = errorCode;
        }
    }
}