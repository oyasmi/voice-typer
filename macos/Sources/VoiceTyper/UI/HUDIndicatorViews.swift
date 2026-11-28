import AppKit
import QuartzCore

// MARK: - 呼吸点

@MainActor
final class PulseDotView: NSView {
    private let circle = CALayer()

    override init(frame: NSRect) {
        super.init(frame: NSRect(x: 0, y: 0, width: 8, height: 8))
        setup()
    }

    required init?(coder: NSCoder) { fatalError() }

    override var intrinsicContentSize: NSSize { NSSize(width: 8, height: 8) }

    override func layout() {
        super.layout()
        circle.frame = bounds
        circle.cornerRadius = min(bounds.width, bounds.height) / 2
    }

    private func setup() {
        wantsLayer = true
        circle.frame = NSRect(x: 0, y: 0, width: 8, height: 8)
        circle.cornerRadius = 4
        circle.backgroundColor = NSColor.systemRed.cgColor
        layer?.addSublayer(circle)
    }

    func startPulse() {
        circle.removeAllAnimations()
        circle.backgroundColor = NSColor.systemRed.cgColor
        let anim = CABasicAnimation(keyPath: "opacity")
        anim.fromValue = 0.5
        anim.toValue = 1.0
        anim.duration = 0.6
        anim.autoreverses = true
        anim.repeatCount = .infinity
        anim.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
        circle.add(anim, forKey: "pulse")
    }

    func stopPulse() {
        circle.removeAllAnimations()
        circle.opacity = 1.0
    }

    /// 换成静态颜色，并带一次颜色过渡（红 → 橙 → 蓝）。
    func setStatic(color: NSColor) {
        let from = circle.presentation()?.backgroundColor ?? circle.backgroundColor
        circle.removeAllAnimations()
        circle.backgroundColor = color.cgColor
        circle.opacity = 1.0
        let transition = CABasicAnimation(keyPath: "backgroundColor")
        transition.fromValue = from
        transition.toValue = color.cgColor
        transition.duration = HUDMotion.colorTransitionDuration
        circle.add(transition, forKey: "colorTransition")
    }
}

// MARK: - 电平驱动声波

@MainActor
final class WaveformView: NSView {
    private let bars: [CALayer] = (0..<5).map { _ in CALayer() }
    private let barWidth: CGFloat = 4
    private let barSpacing: CGFloat = 4
    private let minBarHeight: CGFloat = 3
    private let maxBarHeight: CGFloat = 18
    /// 各条基础权重，制造中间高两侧低的自然形态。
    private let weights: [CGFloat] = [0.55, 0.85, 1.0, 0.7, 0.9]
    /// 快攻慢放后的显示电平（0…1）。
    private var displayLevel: CGFloat = 0
    private var recognizing = false

    override init(frame: NSRect) {
        super.init(frame: frame)
        setup()
    }

    required init?(coder: NSCoder) { fatalError() }

    override var intrinsicContentSize: NSSize { NSSize(width: 44, height: 22) }

    override func layout() {
        super.layout()
        layoutBars()
    }

    private func setup() {
        wantsLayer = true
        bars.forEach { bar in
            bar.cornerRadius = barWidth / 2
            bar.backgroundColor = NSColor(white: 1.0, alpha: 0.9).cgColor
            layer?.addSublayer(bar)
        }
        layoutBars()
    }

    private func layoutBars() {
        let totalWidth = CGFloat(bars.count) * barWidth + CGFloat(bars.count - 1) * barSpacing
        let startX = (bounds.width - totalWidth) / 2
        for (index, bar) in bars.enumerated() {
            let height = heightForBar(index)
            let x = startX + CGFloat(index) * (barWidth + barSpacing)
            let y = (bounds.height - height) / 2
            bar.frame = CGRect(x: x, y: y, width: barWidth, height: height)
        }
    }

    private func heightForBar(_ index: Int) -> CGFloat {
        let base = minBarHeight + (maxBarHeight - minBarHeight) * displayLevel * weights[index]
        return min(max(base, minBarHeight), maxBarHeight)
    }

    /// 进入录音（电平驱动）态。
    func startListening() {
        recognizing = false
        displayLevel = 0
        bars.forEach {
            $0.removeAllAnimations()
            $0.backgroundColor = NSColor(white: 1.0, alpha: 0.9).cgColor
        }
        layoutBars()
    }

    /// 输入线性 RMS 电平，做分贝归一化 + 快攻慢放平滑后更新条高。
    func updateLevel(_ level: Float) {
        guard !recognizing else { return }
        let db = 20 * log10(max(Double(level), 1e-7))
        let normalized = CGFloat(min(max((db + 50) / 40, 0), 1))
        displayLevel = max(normalized, displayLevel * 0.82)

        CATransaction.begin()
        CATransaction.setAnimationDuration(0.08)
        layoutBars()
        CATransaction.commit()
    }

    /// 识别中：先在 0.12s 内收拢到最低高度并由白变橙（"收声"），随后开始柔和的
    /// 确定型顺序脉动。无电平输入，脉动的 `beginTime` 顺延收拢时长。
    func settleThenPulse() {
        recognizing = true
        displayLevel = 0
        let settle = HUDMotion.reduceMotion
            ? min(HUDMotion.barsSettleDuration, HUDMotion.reducedMotionDuration)
            : HUDMotion.barsSettleDuration

        // 条高与颜色的变化交给 CALayer 的隐式动画，放在同一个事务里统一时长。
        CATransaction.begin()
        CATransaction.setAnimationDuration(settle)
        layoutBars()
        bars.forEach { $0.backgroundColor = NSColor.systemOrange.cgColor }
        CATransaction.commit()

        for (index, bar) in bars.enumerated() {
            bar.removeAnimation(forKey: "recognizing")
            guard !HUDMotion.reduceMotion else { continue }
            let anim = CABasicAnimation(keyPath: "transform.scale.y")
            anim.fromValue = 0.4
            anim.toValue = 1.0
            anim.duration = 0.6
            anim.beginTime = CACurrentMediaTime() + settle + Double(index) * 0.09
            anim.autoreverses = true
            anim.repeatCount = .infinity
            anim.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            bar.add(anim, forKey: "recognizing")
        }
    }

    func stop() {
        bars.forEach { $0.removeAllAnimations() }
    }
}
