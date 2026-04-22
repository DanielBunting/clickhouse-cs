using System;
using System.IO;

namespace ClickHouse.Driver.Utility.BlockCompression;

/// <summary>
/// Raised when block-compression framing is malformed, truncated, or fails
/// checksum validation.
/// </summary>
[Serializable]
public class ClickHouseCompressionException : IOException
{
    public ClickHouseCompressionException()
    {
    }

    public ClickHouseCompressionException(string message)
        : base(message)
    {
    }

    public ClickHouseCompressionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
