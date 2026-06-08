namespace CanMessager.Core.Application;

/// <summary>전송 진행 상황 (송/수신 공통). UI 바인딩에 사용.</summary>
public sealed record FileTransferProgress
{
    public required uint TransferId { get; init; }
    public required string FileName { get; init; }
    public required long BytesTransferred { get; init; }
    public required long TotalBytes { get; init; }
    public required double BytesPerSecond { get; init; }
    public required TimeSpan Eta { get; init; }

    public double Percentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100.0 : 0.0;
}

/// <summary>수신 파일 수락 여부를 결정하기 위한 콜백 인자.</summary>
public sealed class IncomingFileEventArgs : EventArgs
{
    public required uint TransferId { get; init; }
    public required string FileName { get; init; }
    public required long TotalBytes { get; init; }

    /// <summary>true로 두면 수락, false로 설정하면 거절. 기본 true(자동 수락).</summary>
    public bool Accept { get; set; } = true;

    /// <summary>저장 경로를 지정하려면 설정. null이면 서비스 기본 디렉터리 사용.</summary>
    public string? SavePath { get; set; }

    /// <summary>거절 시 사유.</summary>
    public CmapReason RejectReason { get; set; } = CmapReason.UserRejected;
}

/// <summary>파일 수신 완료 통지.</summary>
public sealed class FileReceivedEventArgs : EventArgs
{
    public required uint TransferId { get; init; }
    public required string FileName { get; init; }
    public required string SavedPath { get; init; }
    public required long TotalBytes { get; init; }
    public required bool HashVerified { get; init; }
}

/// <summary>전송 실패 예외.</summary>
public sealed class CmapException : Exception
{
    public CmapException(CmapReason reason, string? message = null, Exception? inner = null)
        : base(message ?? reason.ToString(), inner) => Reason = reason;

    public CmapReason Reason { get; }
}
