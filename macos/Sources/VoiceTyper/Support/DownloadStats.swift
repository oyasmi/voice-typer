import Foundation

/// 模型下载的进度详情（R8-C5）：已下载 / 总量、速度、剩余时间。纯值类型，时间由调用方传入，便于单测。
///
/// 总量取 `ModelDownloader` 已 pin 的文件清单之和；已下载 = 进度 × 总量。速度取最近 8s 窗口，
/// 数据不足 1s 或没有进展时不显示速度（不显示比显示一个乱跳的数字好）。
struct DownloadStats {
    static let speedWindow: TimeInterval = 8
    static let minimumSpeedSpan: TimeInterval = 1

    let totalBytes: Int64
    private var samples: [(time: TimeInterval, bytes: Double)] = []

    init(totalBytes: Int64 = ModelDownloader.totalBytes) {
        self.totalBytes = totalBytes
    }

    mutating func reset() {
        samples.removeAll()
    }

    mutating func record(progress: Double, at time: TimeInterval) {
        let bytes = min(max(progress, 0), 1) * Double(totalBytes)
        samples.append((time, bytes))
        samples.removeAll { time - $0.time > Self.speedWindow }
    }

    /// 字节 / 秒；窗口内跨度不足 1s 或没有进展返回 nil。
    var bytesPerSecond: Double? {
        guard let first = samples.first, let last = samples.last else { return nil }
        let span = last.time - first.time
        let delta = last.bytes - first.bytes
        guard span >= Self.minimumSpeedSpan, delta > 0 else { return nil }
        return delta / span
    }

    /// 「已下载 %@ / %@ · %d%% · %@/s，约剩 %@」；缺速度时省略后两段。
    func text(progress: Double) -> String {
        let clamped = min(max(progress, 0), 1)
        let downloaded = Self.byteString(Int64(clamped * Double(totalBytes)))
        let total = Self.byteString(totalBytes)
        let percent = Int(clamped * 100)
        guard let speed = bytesPerSecond else {
            return LF("已下载 %@ / %@ · %d%%", downloaded, total, percent)
        }
        let remainingBytes = max(0, Double(totalBytes) * (1 - clamped))
        return LF(
            "已下载 %@ / %@ · %d%% · %@/s，约剩 %@",
            downloaded, total, percent,
            Self.byteString(Int64(speed)), Self.durationString(seconds: remainingBytes / speed)
        )
    }

    static func byteString(_ bytes: Int64) -> String {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        formatter.allowedUnits = [.useKB, .useMB, .useGB]
        return formatter.string(fromByteCount: bytes)
    }

    static func durationString(seconds: Double) -> String {
        let total = max(1, Int(seconds.rounded(.up)))
        if total < 60 { return LF("%d 秒", total) }
        return LF("%d 分 %d 秒", total / 60, total % 60)
    }
}
