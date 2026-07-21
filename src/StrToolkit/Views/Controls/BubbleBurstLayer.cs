using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace StrToolkit.Views.Controls;

/// <summary>
/// Enter 按钮的轻量气泡爆开动画层。粒子只在约半秒内绘制，不参与命中测试，
/// 也不会阻塞按钮对应的业务操作。
/// </summary>
public sealed class BubbleBurstLayer : Control
{
    private static readonly Color BubbleColor = Color.Parse("#00CED1");
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(560);

    // 粒子参数调整记录（原始规格仍保留在下方 ParticleSpec 中）：
    // 调整前粒子半径为 2.8-5.2；当前按比例映射到明确的 1.0-3.0 区间。
    // 移动调整前：水平 0-13、垂直 24-48、弧度 1.2-2.0。
    // 当前水平、垂直和弧线移动范围均使用原始值的 1/3。
    private const double OriginalMinParticleRadius = 2.8;
    private const double OriginalMaxParticleRadius = 5.2;
    private const double TargetMinParticleRadius = 1.0;
    private const double TargetMaxParticleRadius = 3.0;
    private const double ParticleHorizontalTravelScale = 1.0 / 3.0;
    private const double ParticleVerticalTravelScale = 1.0 / 3.0;
    private const double ParticleArcScale = 1.0 / 3.0;
    private const double RingStrokeThickness = 0.55;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = new();
    private readonly List<Particle> _particles = new(16);

    public BubbleBurstLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnAnimationTick;
    }

    /// <summary>从按钮当前的中心位置启动（或重新启动）一次动画。</summary>
    public void Start(Point buttonCenter, Size buttonSize)
    {
        BuildParticles(buttonCenter, buttonSize);
        _stopwatch.Restart();
        _timer.Stop();
        _timer.Start();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (!_stopwatch.IsRunning || _particles.Count == 0)
        {
            return;
        }

        double overallProgress = Math.Clamp(
            _stopwatch.Elapsed.TotalMilliseconds / AnimationDuration.TotalMilliseconds,
            0,
            1);

        foreach (var particle in _particles)
        {
            double progress = (overallProgress - particle.Delay) / (1 - particle.Delay);
            if (progress is < 0 or > 1)
            {
                continue;
            }

            // 先快速散开，后半段逐渐减速；轻微横向弧度避免机械的直线感。
            double eased = 1 - Math.Pow(1 - progress, 3);
            double arc = Math.Sin(progress * Math.PI) * particle.Arc;
            var center = new Point(
                particle.Start.X + particle.Travel.X * eased + arc,
                particle.Start.Y + particle.Travel.Y * eased);

            double grow = progress < 0.16
                ? 0.55 + progress / 0.16 * 0.45
                : Math.Pow(1 - (progress - 0.16) / 0.84, 0.72);
            double radius = Math.Max(0.1, particle.Radius * grow);
            double opacity = progress < 0.35
                ? 1
                : Math.Clamp(1 - (progress - 0.35) / 0.65, 0, 1);
            byte alpha = (byte)Math.Round(255 * opacity);
            var brush = new SolidColorBrush(Color.FromArgb(
                alpha,
                BubbleColor.R,
                BubbleColor.G,
                BubbleColor.B));

            if (particle.IsRing)
            {
                context.DrawEllipse(
                    null,
                    new Pen(brush, RingStrokeThickness),
                    center,
                    radius,
                    radius);
            }
            else
            {
                context.DrawEllipse(brush, null, center, radius, radius);
            }
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_stopwatch.Elapsed >= AnimationDuration)
        {
            _timer.Stop();
            _stopwatch.Reset();
            _particles.Clear();
        }
        InvalidateVisual();
    }

    private void BuildParticles(Point center, Size buttonSize)
    {
        _particles.Clear();

        double halfWidth = Math.Max(30, buttonSize.Width / 2);
        double halfHeight = Math.Max(14, buttonSize.Height / 2);

        // 与 Electron 版接近：上方 9 颗、下方 7 颗，混合实心圆和空心圆环。
        AddRow(center, halfWidth, -halfHeight, -1, new[]
        {
            new ParticleSpec(-0.86, -12, 25, 3.2, false, 0.00, -2.0),
            new ParticleSpec(-0.64, -10, 43, 5.2, true,  0.03,  2.0),
            new ParticleSpec(-0.43,  -6, 34, 3.8, false, 0.01, -1.6),
            new ParticleSpec(-0.22,  -3, 48, 4.8, false, 0.05,  1.4),
            new ParticleSpec( 0.00,   0, 37, 4.2, true,  0.00, -1.2),
            new ParticleSpec( 0.23,   4, 30, 2.9, false, 0.06,  1.8),
            new ParticleSpec( 0.45,   7, 42, 4.0, false, 0.02, -1.5),
            new ParticleSpec( 0.66,  10, 35, 3.1, false, 0.04,  1.4),
            new ParticleSpec( 0.87,  13, 27, 4.6, false, 0.01, -2.0)
        });

        AddRow(center, halfWidth, halfHeight, 1, new[]
        {
            new ParticleSpec(-0.76, -12, 25, 3.8, false, 0.02,  1.7),
            new ParticleSpec(-0.51,  -8, 34, 5.0, false, 0.05, -1.5),
            new ParticleSpec(-0.25,  -4, 27, 3.2, true,  0.00,  1.3),
            new ParticleSpec( 0.00,   0, 36, 4.5, false, 0.04, -1.2),
            new ParticleSpec( 0.28,   5, 29, 3.5, false, 0.01,  1.6),
            new ParticleSpec( 0.55,   9, 33, 2.8, false, 0.06, -1.4),
            new ParticleSpec( 0.79,  12, 24, 4.4, false, 0.02,  1.8)
        });
    }

    private void AddRow(
        Point center,
        double halfWidth,
        double startY,
        int verticalDirection,
        IReadOnlyList<ParticleSpec> specs)
    {
        foreach (var spec in specs)
        {
            _particles.Add(new Particle(
                new Point(center.X + halfWidth * spec.HorizontalPosition, center.Y + startY),
                new Vector(
                    spec.HorizontalTravel * ParticleHorizontalTravelScale,
                    spec.VerticalTravel * verticalDirection * ParticleVerticalTravelScale),
                MapParticleRadius(spec.Radius),
                spec.IsRing,
                spec.Delay,
                spec.Arc * ParticleArcScale));
        }
    }

    private static double MapParticleRadius(double originalRadius)
    {
        double ratio = (originalRadius - OriginalMinParticleRadius) /
                       (OriginalMaxParticleRadius - OriginalMinParticleRadius);
        return TargetMinParticleRadius +
               Math.Clamp(ratio, 0, 1) * (TargetMaxParticleRadius - TargetMinParticleRadius);
    }

    private sealed record Particle(
        Point Start,
        Vector Travel,
        double Radius,
        bool IsRing,
        double Delay,
        double Arc);

    private sealed record ParticleSpec(
        double HorizontalPosition,
        double HorizontalTravel,
        double VerticalTravel,
        double Radius,
        bool IsRing,
        double Delay,
        double Arc);
}
