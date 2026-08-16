import Foundation

/// 极简 WAV 读取：只支持本项目测试夹具用到的形态（PCM float32 或 int16，单声道）。
/// 不依赖 AVFoundation，纯按 RIFF chunk 结构解析，避免系统解码器的隐式重采样掩盖问题。
enum WavLoader {
    struct Error: Swift.Error { let message: String }

    static func loadMonoFloat32(_ url: URL) throws -> (samples: [Float], sampleRate: Int) {
        let data = try Data(contentsOf: url)
        guard data.count > 44,
              data[0..<4].elementsEqual("RIFF".utf8),
              data[8..<12].elementsEqual("WAVE".utf8) else {
            throw Error(message: "不是合法的 RIFF/WAVE 文件: \(url.lastPathComponent)")
        }

        var offset = 12
        var audioFormat: UInt16 = 0
        var channels: UInt16 = 0
        var sampleRate: UInt32 = 0
        var bitsPerSample: UInt16 = 0
        var dataRange: Range<Int>?

        while offset + 8 <= data.count {
            let chunkID = data[offset..<(offset + 4)]
            let chunkSize = Int(readUInt32(data, offset + 4))
            let bodyStart = offset + 8
            let bodyEnd = min(bodyStart + chunkSize, data.count)

            if chunkID.elementsEqual("fmt ".utf8) {
                audioFormat = readUInt16(data, bodyStart)
                channels = readUInt16(data, bodyStart + 2)
                sampleRate = readUInt32(data, bodyStart + 4)
                bitsPerSample = readUInt16(data, bodyStart + 14)
            } else if chunkID.elementsEqual("data".utf8) {
                dataRange = bodyStart..<bodyEnd
            }
            offset = bodyEnd + (chunkSize % 2) // chunk 按偶数对齐
        }

        guard let dataRange else { throw Error(message: "未找到 data chunk") }
        guard channels == 1 else { throw Error(message: "仅支持单声道，实际 \(channels) 声道") }

        let raw = data[dataRange]
        var samples: [Float] = []

        if audioFormat == 3, bitsPerSample == 32 {
            samples = raw.withUnsafeBytes { buf in
                Array(buf.bindMemory(to: Float.self))
            }
        } else if audioFormat == 1, bitsPerSample == 16 {
            let ints: [Int16] = raw.withUnsafeBytes { buf in
                Array(buf.bindMemory(to: Int16.self))
            }
            samples = ints.map { Float($0) / 32768.0 }
        } else {
            throw Error(message: "不支持的格式: format=\(audioFormat) bits=\(bitsPerSample)")
        }

        return (samples, Int(sampleRate))
    }

    private static func readUInt32(_ data: Data, _ offset: Int) -> UInt32 {
        let bytes = data[offset..<(offset + 4)]
        return bytes.withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }
    }

    private static func readUInt16(_ data: Data, _ offset: Int) -> UInt16 {
        let bytes = data[offset..<(offset + 2)]
        return bytes.withUnsafeBytes { $0.loadUnaligned(as: UInt16.self) }
    }
}
