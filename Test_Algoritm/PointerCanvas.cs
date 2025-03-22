using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Glyph;

namespace Test_Algoritm
{
    public class PointerCanvas : Control
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _events;
        private IDisposable? _statusUpdated;
        private readonly CanvasSheet _canvasSheet = new();
        private readonly Dictionary<int, Stroke> _activeStrokes = new();
        private readonly Dictionary<Stroke, StreamGeometry> _geometryCache = new();
        private PointerPointProperties? _lastProperties;
        private PointerUpdateKind? _lastNonOtherUpdateKind;
        private bool _drawOnlyPoints;
        private bool _isRendering;
        private readonly object _renderLock = new();

        public static readonly DirectProperty<PointerCanvas, bool> DrawOnlyPointsProperty =
            AvaloniaProperty.RegisterDirect<PointerCanvas, bool>(nameof(DrawOnlyPoints), c => c.DrawOnlyPoints, (c, v) => c.DrawOnlyPoints = v);

        public bool DrawOnlyPoints
        {
            get => _drawOnlyPoints;
            set => SetAndRaise(DrawOnlyPointsProperty, ref _drawOnlyPoints, value);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _statusUpdated = DispatcherTimer.Run(() =>
            {
                if (_stopwatch.Elapsed.TotalMilliseconds > 250)
                {
                    Status = $@"Events per second: {(_events / _stopwatch.Elapsed.TotalSeconds)}
PointerUpdateKind: {_lastProperties?.PointerUpdateKind}
Last PointerUpdateKind != Other: {_lastNonOtherUpdateKind}
IsLeftButtonPressed: {_lastProperties?.IsLeftButtonPressed}
IsRightButtonPressed: {_lastProperties?.IsRightButtonPressed}
IsMiddleButtonPressed: {_lastProperties?.IsMiddleButtonPressed}
IsXButton1Pressed: {_lastProperties?.IsXButton1Pressed}
IsXButton2Pressed: {_lastProperties?.IsXButton2Pressed}
IsBarrelButtonPressed: {_lastProperties?.IsBarrelButtonPressed}
IsEraser: {_lastProperties?.IsEraser}
IsInverted: {_lastProperties?.IsInverted}
Pressure: {_lastProperties?.Pressure}
XTilt: {_lastProperties?.XTilt}
YTilt: {_lastProperties?.YTilt}
Twist: {_lastProperties?.Twist}";
                    _stopwatch.Restart();
                    _events = 0;
                }
                return true;
            }, TimeSpan.FromMilliseconds(100));
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _statusUpdated?.Dispose();
        }

        private string? _status;
        public static readonly DirectProperty<PointerCanvas, string?> StatusProperty =
            AvaloniaProperty.RegisterDirect<PointerCanvas, string?>(nameof(Status), c => c.Status, (c, v) => c.Status = v);

        public string? Status
        {
            get => _status;
            set => SetAndRaise(StatusProperty, ref _status, value);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                lock (_renderLock)
                {
                    _canvasSheet.Strokes.Clear();
                    _activeStrokes.Clear();
                    _geometryCache.Clear();
                }
                InvalidateVisual();
                return;
            }

            HandleEvent(e);
            base.OnPointerPressed(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            HandleEvent(e);
            base.OnPointerMoved(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            HandleEvent(e);
            if (_activeStrokes.TryGetValue(e.Pointer.Id, out var stroke))
            {
                _canvasSheet.Strokes.Add(stroke);
                _activeStrokes.Remove(e.Pointer.Id);
            }
            base.OnPointerReleased(e);
        }

        private void HandleEvent(PointerEventArgs e)
        {
            _events++;

            var currentPoint = e.GetCurrentPoint(this);
            _lastProperties = currentPoint.Properties;

            if (_lastProperties?.PointerUpdateKind != PointerUpdateKind.Other)
            {
                _lastNonOtherUpdateKind = _lastProperties?.PointerUpdateKind;
            }

            if (e.Pointer.Type != PointerType.Pen || currentPoint.Properties.Pressure > 0)
            {
                if (!_activeStrokes.TryGetValue(e.Pointer.Id, out var stroke))
                {
                    stroke = new Stroke();
                    _activeStrokes[e.Pointer.Id] = stroke;
                }

                float pressure = Math.Max(0.1f, currentPoint.Properties.Pressure);

                // Always add the current point
                var point = new BezierPoint(
                    (float)currentPoint.Position.X,
                    (float)currentPoint.Position.Y,
                    pressure
                );
                stroke.Points.Add(point);

                // If it's a move event, also add intermediate points
                if (e.RoutedEvent == PointerMovedEvent)
                {
                    var pts = e.GetIntermediatePoints(this);
                    if (pts.Count > 0)
                    {
                        foreach (var p in pts)
                        {
                            float pointPressure = Math.Max(0.1f, p.Properties.Pressure);
                            var bezierPoint = new BezierPoint(
                                (float)p.Position.X,
                                (float)p.Position.Y,
                                pointPressure
                            );
                            stroke.Points.Add(bezierPoint);
                        }
                    }
                }

                // Clear the geometry cache for this stroke since it's been modified
                _geometryCache.Remove(stroke);
            }

            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (_isRendering) return;
            _isRendering = true;

            try
            {
                context.FillRectangle(Brushes.White, Bounds);
                
                lock (_renderLock)
                {
                    // Draw completed strokes
                    var allStrokes = _canvasSheet.Strokes.ToList();
                    allStrokes.AddRange(_activeStrokes.Values);
                    
                    foreach (var stroke in allStrokes)
                    {
                        RenderStroke(context, stroke);
                    }
                }
            }
            finally
            {
                _isRendering = false;
            }

            base.Render(context);
        }

        private void RenderStroke(DrawingContext context, Stroke stroke)
        {
            if (stroke.Points.Count < 2) return;

            Debug.WriteLine($"Rendering stroke with {stroke.Points.Count} points");

            if (_drawOnlyPoints)
            {
                foreach (var point in stroke.Points)
                {
                    context.DrawEllipse(
                        Brushes.Black,
                        null,
                        new Point(point.X, point.Y),
                        Math.Max(1, point.Thickness),
                        Math.Max(1, point.Thickness)
                    );
                }
                return;
            }

            // Try to get cached geometry
            if (!_geometryCache.TryGetValue(stroke, out var geometry))
            {
                geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    var points = stroke.Points;
                    ctx.BeginFigure(new Point(points[0].X, points[0].Y), false);

                    if (points.Count == 2)
                    {
                        ctx.LineTo(new Point(points[1].X, points[1].Y));
                    }
                    else
                    {
                        // Draw as a series of connected lines for smoother appearance
                        for (int i = 1; i < points.Count; i++)
                        {
                            ctx.LineTo(new Point(points[i].X, points[i].Y));
                        }
                    }
                    ctx.EndFigure(false);
                }
                _geometryCache[stroke] = geometry;
            }

            // Use a single pen for all strokes with proper thickness based on pressure
            var pen = new Pen(
                Brushes.Black,
                Math.Max(1, stroke.Points[0].Thickness * 2), // Multiply thickness by 2 for better visibility
                null,
                PenLineCap.Round,
                PenLineJoin.Round
            );

            context.DrawGeometry(null, pen, geometry);
        }
    }
}
