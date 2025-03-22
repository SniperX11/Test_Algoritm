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
using System.Text.Json;
using System.IO;

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

        public PointerCanvas()
        {
            Focusable = true;
        }

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

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.None)
            {
                SaveDrawingToJson();
            }
            base.OnKeyDown(e);
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

            // Only process events when pointer is pressed down
            bool isPointerDown = currentPoint.Properties.IsLeftButtonPressed || 
                                currentPoint.Properties.IsRightButtonPressed || 
                                currentPoint.Properties.IsBarrelButtonPressed;
            
            if (!isPointerDown && e.RoutedEvent != PointerPressedEvent)
            {
                return;
            }

            if (e.Pointer.Type != PointerType.Pen || currentPoint.Properties.Pressure > 0)
            {
                if (!_activeStrokes.TryGetValue(e.Pointer.Id, out var stroke))
                {
                    stroke = new Stroke();
                    // Start with default tension
                    stroke.Tension = 0.5f;
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

                // Dynamically adjust tension based on stroke characteristics
                if (stroke.Points.Count >= 3)
                {
                    AdjustTensionDynamically(stroke);
                }

                // Clear the geometry cache for this stroke since it's been modified
                _geometryCache.Remove(stroke);
            }

            InvalidateVisual();
        }

        private void AdjustTensionDynamically(Stroke stroke)
        {
            // We need at least 3 points to calculate meaningful metrics
            if (stroke.Points.Count < 3)
                return;

            // Calculate average distance between points
            float totalDistance = 0;
            float maxDistance = 0;
            float avgCurvature = 0;
            int curvatureCount = 0;

            // Get the last few points for analysis (max 10 points)
            int startIdx = Math.Max(0, stroke.Points.Count - 10);
            var analysisPoints = stroke.Points.Skip(startIdx).ToList();

            // Calculate distance metrics
            for (int i = 1; i < analysisPoints.Count; i++)
            {
                var p1 = analysisPoints[i - 1];
                var p2 = analysisPoints[i];
                
                float dx = p2.X - p1.X;
                float dy = p2.Y - p1.Y;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                
                totalDistance += distance;
                maxDistance = Math.Max(maxDistance, distance);
            }

            // Calculate curvature metrics (need at least 3 points)
            for (int i = 2; i < analysisPoints.Count; i++)
            {
                var p1 = analysisPoints[i - 2];
                var p2 = analysisPoints[i - 1];
                var p3 = analysisPoints[i];
                
                // Calculate angle between segments
                float angle = CalculateAngle(p1, p2, p3);
                avgCurvature += angle;
                curvatureCount++;
            }

            // Normalize metrics
            float avgDistance = totalDistance / (analysisPoints.Count - 1);
            avgCurvature = curvatureCount > 0 ? avgCurvature / curvatureCount : 0;

            // Speed factor (faster = higher tension)
            float speedFactor = Math.Min(1.0f, avgDistance / 10.0f);
            
            // Curvature factor (higher curvature = lower tension)
            float curvatureFactor = 1.0f - Math.Min(1.0f, avgCurvature / 90.0f);
            
            // Distance factor (larger distances = higher tension)
            float distanceFactor = Math.Min(1.0f, maxDistance / 50.0f);

            // Apply weighted factors to calculate final tension
            float baseTension = 0.4f; // Base tension value
            float tensionRange = 0.5f; // Maximum adjustment
            
            // Calculate new tension with weights for each factor
            float newTension = baseTension + 
                              (0.4f * speedFactor) + 
                              (0.4f * curvatureFactor) + 
                              (0.2f * distanceFactor);
            
            // Clamp to reasonable range
            newTension = Math.Clamp(newTension, 0.2f, 0.9f);
            
            // Apply smoothing to avoid sudden changes
            float smoothingFactor = 0.3f;
            stroke.Tension = stroke.Tension * (1 - smoothingFactor) + newTension * smoothingFactor;
        }

        private float CalculateAngle(BezierPoint p1, BezierPoint p2, BezierPoint p3)
        {
            // Calculate vectors
            float dx1 = p2.X - p1.X;
            float dy1 = p2.Y - p1.Y;
            float dx2 = p3.X - p2.X;
            float dy2 = p3.Y - p2.Y;
            
            // Calculate lengths
            float len1 = (float)Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            float len2 = (float)Math.Sqrt(dx2 * dx2 + dy2 * dy2);
            
            // Avoid division by zero
            if (len1 < 0.0001f || len2 < 0.0001f)
                return 0f;
            
            // Normalize vectors
            dx1 /= len1;
            dy1 /= len1;
            dx2 /= len2;
            dy2 /= len2;
            
            // Calculate dot product
            float dotProduct = dx1 * dx2 + dy1 * dy2;
            
            // Calculate angle in degrees
            float angle = (float)Math.Acos(Math.Clamp(dotProduct, -1f, 1f)) * 180f / (float)Math.PI;
            
            return angle;
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
                    var tension = stroke.Tension;
                    
                    ctx.BeginFigure(new Point(points[0].X, points[0].Y), false);

                    if (points.Count == 2)
                    {
                        // For just two points, draw a straight line
                        ctx.LineTo(new Point(points[1].X, points[1].Y));
                    }
                    else
                    {
                        // Process points to identify sharp angles vs. smooth curves
                        for (int i = 1; i < points.Count; i++)
                        {
                            var prevPoint = i > 1 ? points[i - 2] : points[0];
                            var currentPoint = points[i - 1];
                            var nextPoint = points[i];
                            
                            // Calculate angle between segments to detect sharp angles
                            bool isSharpAngle = IsSharpAngle(prevPoint, currentPoint, nextPoint);
                            
                            if (isSharpAngle)
                            {
                                // For sharp angles, use straight lines
                                ctx.LineTo(new Point(currentPoint.X, currentPoint.Y));
                                if (i == points.Count - 1) {
                                    ctx.LineTo(new Point(nextPoint.X, nextPoint.Y));
                                }
                            }
                            else if (i == points.Count - 1)
                            {
                                // Last point, draw a line to it
                                ctx.LineTo(new Point(nextPoint.X, nextPoint.Y));
                            }
                            else
                            {
                                // For smooth parts, use cubic bezier for a cursive-like appearance
                                var p0 = currentPoint;
                                var p1 = nextPoint;
                                var p2 = i < points.Count - 2 ? points[i + 1] : nextPoint;
                                
                                // Calculate control points based on tension and surrounding points
                                var dx1 = p1.X - p0.X;
                                var dy1 = p1.Y - p0.Y;
                                var dx2 = p2.X - p1.X;
                                var dy2 = p2.Y - p1.Y;
                                
                                var cp1 = new Point(
                                    p0.X + dx1 * tension,
                                    p0.Y + dy1 * tension
                                );
                                
                                var cp2 = new Point(
                                    p1.X - dx2 * tension * 0.5f,
                                    p1.Y - dy2 * tension * 0.5f
                                );
                                
                                ctx.CubicBezierTo(cp1, cp2, new Point(p1.X, p1.Y));
                                i++; // Skip the next point as we've used it for the curve
                            }
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

        private bool IsSharpAngle(BezierPoint p1, BezierPoint p2, BezierPoint p3)
        {
            // Calculate vectors
            float dx1 = p2.X - p1.X;
            float dy1 = p2.Y - p1.Y;
            float dx2 = p3.X - p2.X;
            float dy2 = p3.Y - p2.Y;
            
            // Calculate lengths
            float len1 = (float)Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            float len2 = (float)Math.Sqrt(dx2 * dx2 + dy2 * dy2);
            
            // Avoid division by zero
            if (len1 < 0.0001f || len2 < 0.0001f)
                return false;
            
            // Normalize vectors
            dx1 /= len1;
            dy1 /= len1;
            dx2 /= len2;
            dy2 /= len2;
            
            // Calculate dot product
            float dotProduct = dx1 * dx2 + dy1 * dy2;
            
            // Calculate angle in degrees
            float angle = (float)Math.Acos(Math.Clamp(dotProduct, -1f, 1f)) * 180f / (float)Math.PI;
            
            // Consider it sharp if angle is greater than threshold
            return angle > 45f; // Threshold for sharp angles (45 degrees)
        }

        private void SaveDrawingToJson()
        {
            try
            {
                var drawingData = new
                {
                    Strokes = _canvasSheet.Strokes.Select(stroke => new
                    {
                        Points = stroke.Points.Select(point => new
                        {
                            X = point.X,
                            Y = point.Y,
                            Thickness = point.Thickness
                        }).ToList()
                    }).ToList()
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                string jsonString = JsonSerializer.Serialize(drawingData, options);
                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "drawing.json");
                File.WriteAllText(filePath, jsonString);
                
                Debug.WriteLine($"Drawing saved to: {filePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving drawing: {ex.Message}");
            }
        }
    }
}
