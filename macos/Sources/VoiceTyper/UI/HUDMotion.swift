import AppKit
import QuartzCore

/// 浮窗动效的参数与动画工厂，集中放在这里便于统一调参与测试。
///
/// 约定：
/// - 所有缩放 / 位移都只是 presentation 层动画（`isRemovedOnCompletion = true`，不写 model 值）。
///   AppKit 管理 layer-backed 视图的几何，写 `layer.transform` 会在下一次布局时被覆盖或错位。
/// - 「减少动态效果」开启时退化为不超过 0.15s 的淡入淡出：不缩放、不位移、不弹簧、不流光。
///   每次开始动画前现读，能即时响应系统设置变化。
@MainActor
enum HUDMotion {
    /// 测试可替换。
    static var reduceMotionProvider: () -> Bool = { NSWorkspace.shared.accessibilityDisplayShouldReduceMotion }

    static var reduceMotion: Bool { reduceMotionProvider() }

    /// 「呼气」：录音结束进入识别时，前景内容 1 → 0.985 → 1。
    static let exhaleScale: CGFloat = 0.985
    static let exhaleDuration: CFTimeInterval = 0.26
    /// 波形五根条收拢到最低高度并由白变橙的时间；之后才开始顺序脉动。
    static let barsSettleDuration: CFTimeInterval = 0.12
    /// 弹簧参数：约 0.28s 稳定、轻微回弹。
    static let springDamping: CGFloat = 14
    static let springStiffness: CGFloat = 220
    static let springMass: CGFloat = 1
    static let popInStartScale: CGFloat = 0.6
    static let shimmerDuration: CFTimeInterval = 1.4
    static let textRiseDuration: CFTimeInterval = 0.18
    static let textRiseOffset: CGFloat = 2
    static let colorDuration: CFTimeInterval = 0.2
    /// 减少动态效果时所有过渡的时长上限。
    static let reducedMotionDuration: CFTimeInterval = 0.15

    /// 以 bounds 中心为原点的缩放。AppKit 视图的 layer anchorPoint 是左下角，
    /// 直接 scale 会向左下缩，所以要平移到中心、缩放、再平移回来。
    static func centeredScale(_ scale: CGFloat, in bounds: CGRect) -> CATransform3D {
        let center = CGPoint(x: bounds.midX, y: bounds.midY)
        var transform = CATransform3DMakeTranslation(center.x, center.y, 0)
        transform = CATransform3DScale(transform, scale, scale, 1)
        return CATransform3DTranslate(transform, -center.x, -center.y, 0)
    }

    /// 颜色过渡时长。
    static var colorTransitionDuration: CFTimeInterval {
        reduceMotion ? reducedMotionDuration : colorDuration
    }

    static func exhaleAnimation(bounds: CGRect) -> CAAnimation? {
        guard !reduceMotion else { return nil }
        let animation = CAKeyframeAnimation(keyPath: "transform")
        animation.values = [
            CATransform3DIdentity,
            centeredScale(exhaleScale, in: bounds),
            CATransform3DIdentity,
        ].map { NSValue(caTransform3D: $0) }
        animation.keyTimes = [0, 0.5, 1]
        animation.timingFunctions = [
            CAMediaTimingFunction(name: .easeInEaseOut),
            CAMediaTimingFunction(name: .easeInEaseOut),
        ]
        animation.duration = exhaleDuration
        return animation
    }

    /// 结果图标弹入：缩放 0.6 → 1（弹簧）+ 透明度 0 → 1。
    static func popInAnimation(bounds: CGRect) -> CAAnimation {
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = 0
        fade.toValue = 1
        guard !reduceMotion else {
            fade.duration = reducedMotionDuration
            return fade
        }

        let spring = CASpringAnimation(keyPath: "transform")
        spring.fromValue = NSValue(caTransform3D: centeredScale(popInStartScale, in: bounds))
        spring.toValue = NSValue(caTransform3D: CATransform3DIdentity)
        spring.damping = springDamping
        spring.stiffness = springStiffness
        spring.mass = springMass
        spring.duration = spring.settlingDuration
        fade.duration = min(spring.duration, 0.2)

        let group = CAAnimationGroup()
        group.animations = [spring, fade]
        group.duration = spring.duration
        return group
    }

    /// 结果态状态文字：从下方 2pt 处上移到位 + 淡入。
    static func textRiseAnimation() -> CAAnimation {
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = 0
        fade.toValue = 1
        guard !reduceMotion else {
            fade.duration = reducedMotionDuration
            return fade
        }

        let rise = CABasicAnimation(keyPath: "transform.translation.y")
        rise.fromValue = -textRiseOffset
        rise.toValue = 0
        rise.timingFunction = CAMediaTimingFunction(name: .easeOut)

        let group = CAAnimationGroup()
        group.animations = [rise, fade]
        group.duration = textRiseDuration
        return group
    }

    /// 「校对中…」文字上的灰阶流光：设为 `layer.mask`，三段 alpha 0.45 / 1.0 / 0.45
    /// 从左扫到右并无限循环。减少动态效果时返回 nil，文字保持静态。
    static func shimmerMask(for bounds: CGRect) -> CAGradientLayer? {
        guard !reduceMotion else { return nil }
        let mask = CAGradientLayer()
        mask.frame = bounds
        mask.startPoint = CGPoint(x: 0, y: 0.5)
        mask.endPoint = CGPoint(x: 1, y: 0.5)
        mask.colors = [0.45, 1.0, 0.45].map { NSColor(white: 1, alpha: $0).cgColor }
        mask.locations = [-0.3, -0.15, 0]

        let sweep = CABasicAnimation(keyPath: "locations")
        sweep.fromValue = [-0.3, -0.15, 0]
        sweep.toValue = [1, 1.15, 1.3]
        sweep.duration = shimmerDuration
        sweep.repeatCount = .infinity
        sweep.timingFunction = CAMediaTimingFunction(name: .linear)
        mask.add(sweep, forKey: "shimmer")
        return mask
    }
}
