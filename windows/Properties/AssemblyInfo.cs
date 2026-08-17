using System.Runtime.CompilerServices;

// 让 Tests/VoiceTyper.Tests 能访问本项目的 internal 类型（ASR/配置等均未标记 public，
// 与 macos/ 的 @testable import VoiceTyper 是同一个思路）。
[assembly: InternalsVisibleTo("VoiceTyper.Tests")]
