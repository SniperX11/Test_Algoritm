using System.Collections.Generic;
using System.Linq;
using ProtoBuf;

namespace Glyph
{
    [ProtoContract]
    public class CanvasSheet : Sheet
    {
        [ProtoMember(1)]
        public List<Stroke> Strokes { get; set; } = new();
    }

    [ProtoContract]
    public class Stroke
    {
        [ProtoMember(1)]
        public List<BezierPoint> Points { get; set; } = new();
        
        /// <summary>
        /// Controls how angular the curve is (0 = straight lines, 1 = smooth curves)
        /// </summary>
        [ProtoMember(2)]
        public float Tension { get; set; } = 0.5f; 
        
        public Stroke() {}

        public Stroke(float tension, params BezierPoint[] points)
        {
            Tension = tension;
            Points = points.ToList();
        }
    }

    [ProtoContract]
    public class BezierPoint
    {
        [ProtoMember(1)]
        public float X { get; set; }
        
        [ProtoMember(2)]
        public float Y { get; set; }
        
        [ProtoMember(3)]
        public float Thickness { get; set; } // Optional per-point thickness override
        
        public BezierPoint() {}
        
        public BezierPoint(float x, float y)
        {
            X = x;
            Y = y;
            Thickness = 0.85f;
        }
        
        public BezierPoint(float x, float y, float thickness)
        {
            X = x;
            Y = y;
            Thickness = thickness;
        }
    }
} 