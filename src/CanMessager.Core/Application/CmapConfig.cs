namespace CanMessager.Core.Application;

/// <summary>CMAP 동작 파라미터. docs/02 2장, 5.3.</summary>
public sealed record CmapConfig
{
    /// <summary>
    /// 청크 크기 (byte). 한 청크 = 한 ISO-TP 메시지.
    /// 클수록 청크당 ISO-TP FC 왕복(메시지 간 간격)이 줄어 처리량↑. 기본 64 KiB.
    /// </summary>
    public int ChunkSize { get; init; } = 64 * 1024;

    /// <summary>윈도우(블록당 청크 수). 기본 32.</summary>
    public ushort WindowSize { get; init; } = 32;

    /// <summary>DATA에 청크별 CRC32를 포함할지. 기본 true.</summary>
    public bool UseChunkCrc { get; init; } = true;

    /// <summary>HELLO_ACK 대기 (ms).</summary>
    public int HelloTimeoutMs { get; init; } = 3000;

    /// <summary>META_ACK 대기 (ms).</summary>
    public int MetaTimeoutMs { get; init; } = 3000;

    /// <summary>블록 ACK 대기 (ms).</summary>
    public int AckTimeoutMs { get; init; } = 5000;

    /// <summary>FIN_ACK 대기 (ms).</summary>
    public int FinTimeoutMs { get; init; } = 5000;

    /// <summary>블록 재전송 최대 횟수.</summary>
    public int MaxRetriesPerBlock { get; init; } = 5;

    /// <summary>핸드셰이크(HELLO/META) 재시도 최대 횟수.</summary>
    public int MaxHandshakeRetries { get; init; } = 3;

    /// <summary>진행률 이벤트 최소 발생 간격 (ms). 0이면 매 청크.</summary>
    public int ProgressIntervalMs { get; init; } = 200;
}
