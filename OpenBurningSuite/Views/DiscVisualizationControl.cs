// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace OpenBurningSuite.Views;

/// <summary>
/// Real-time disc burning and verifying visualization control that converts
/// LBA progress to physical disc coordinates using the spiral geometry of
/// optical media. Renders using SkiaSharp via Avalonia's ICustomDrawOperation.
///
/// Optical disc geometry (ECMA-120, ECMA-279, ECMA-359, ECMA-377):
///   - CD:     spiral from r ≈ 25 mm to r ≈ 58 mm, track pitch 1.6 µm
///   - DVD:    spiral from r ≈ 24 mm to r ≈ 58 mm, track pitch 0.74 µm
///   - HD DVD: spiral from r ≈ 24 mm to r ≈ 58 mm, track pitch 0.40 µm
///   - BD:     spiral from r ≈ 24 mm to r ≈ 58 mm, track pitch 0.32 µm
///
/// The visualization maps the LBA range [0, TotalSectors] onto a spiral that
/// starts at the inner radius and winds outward to the outer radius. The
/// angular position at each LBA is derived from the Archimedean spiral
/// equation: r(θ) = r_inner + (trackPitch / 2π) × θ. The total number of
/// revolutions N is (r_outer - r_inner) / trackPitch. The arc length at
/// a given radius determines the number of sectors per revolution, which
/// varies with radius (CLV/ZCLV). For visualization accuracy, sectors are
/// evenly distributed across the total spiral length.
/// </summary>
public class DiscVisualizationControl : Control
{
    // -----------------------------------------------------------------------
    //  Public state — update from UI thread
    // -----------------------------------------------------------------------

    /// <summary>Current LBA position being written or verified.</summary>
    public long CurrentLba { get; set; }

    /// <summary>Total number of sectors on the disc or in the image.</summary>
    public long TotalSectors { get; set; }

    /// <summary>Current operation percentage (0–100).</summary>
    public int PercentComplete { get; set; }

    /// <summary>Current write/verify speed multiplier (e.g. 8.0x).</summary>
    public double CurrentSpeedX { get; set; }

    /// <summary>
    /// Operation mode displayed in the visualization.
    /// </summary>
    public DiscOperationMode OperationMode { get; set; } = DiscOperationMode.Burning;

    /// <summary>Number of bad sectors detected (verify mode).</summary>
    public long BadSectors { get; set; }

    /// <summary>Whether the operation is currently active.</summary>
    public bool IsActive { get; set; }

    /// <summary>Whether the operation has completed.</summary>
    public bool IsCompleted { get; set; }

    /// <summary>Whether the operation result was a pass (verify mode).</summary>
    public bool? VerifyPassed { get; set; }

    // -----------------------------------------------------------------------
    //  Multi-layer disc properties
    //
    //  DVD DL (ECMA-279): 2 layers, OTP (video) or PTP (data)
    //  BD DL  (ECMA-359): 2 layers, always PTP (inner→outer on both)
    //  BDXL TL/QL:        3–4 layers, always PTP
    //  HD DVD DL:         2 layers, OTP or PTP
    //  CD:                always single layer
    // -----------------------------------------------------------------------

    /// <summary>
    /// Number of recording layers (1 = single-layer, 2 = dual-layer, 3 = TL, 4 = QL).
    /// Default is 1 (single-layer). Set to &gt;1 for layered media visualization.
    /// </summary>
    public int LayerCount { get; set; } = 1;

    /// <summary>
    /// Track path type for multi-layer media. Only relevant when LayerCount &gt; 1.
    /// OTP (Opposite Track Path): Layer 0 inner→outer, Layer 1 outer→inner (DVD-Video DL).
    /// PTP (Parallel Track Path): All layers inner→outer (BD DL, BDXL, DVD-ROM DL).
    /// </summary>
    public TrackPathType TrackPath { get; set; } = TrackPathType.PTP;

    /// <summary>
    /// LBA position of the layer break (end of layer 0). Only relevant when LayerCount &gt; 1.
    /// When set to 0, the layer break is assumed to be at TotalSectors / LayerCount.
    /// </summary>
    public long LayerBreakLba { get; set; }

    // -----------------------------------------------------------------------
    //  Disc physical geometry constants (normalized to unit disc)
    //
    //  Actual physical dimensions per ECMA standards:
    //    CD  (ECMA-120):  inner radius 25 mm, outer 58 mm, pitch 1.6 µm
    //    DVD (ECMA-279):  inner radius 24 mm, outer 58 mm, pitch 0.74 µm
    //    HD DVD (ECMA-377): inner radius 24 mm, outer 58 mm, pitch 0.40 µm
    //    BD  (ECMA-359):  inner radius 24 mm, outer 58 mm, pitch 0.32 µm
    //
    //  We normalize to [0, 1] disc radius for rendering:
    //    Inner hub radius = ~0.38 (visual clamping ring + hub)
    //    Data start       = ~0.42 (lead-in area start)
    //    Data end         = ~0.95 (lead-out area end)
    //    Outer rim        =  1.00
    // -----------------------------------------------------------------------

    private const float HubRadius = 0.18f;            // Central clamping hub
    private const float InnerRingRadius = 0.36f;       // Mirror/stacking ring
    private const float DataStartRadius = 0.40f;       // Data area inner edge
    private const float DataEndRadius = 0.93f;         // Data area outer edge
    private const float OuterRimRadius = 1.00f;        // Physical disc edge

    // Visual spiral: number of visible "track rings" in the visualization.
    // Real discs have tens of thousands of revolutions, but we compress to
    // a visually distinguishable count for rendering clarity.
    private const int VisualRevolutions = 140;

    // Multi-layer rendering constants
    private const float LayerGapFraction = 0.02f;         // Visual gap between layers (fraction of data area)
    private const int MinRevsPerLayer = 20;                // Minimum spiral revolutions per layer
    private const float LayerBoundaryDashLength = 4f;      // Dash length for layer boundary ring
    private const float LayerBoundaryDashGap = 3f;         // Gap length for layer boundary ring
    private const float GradientRadiusScale = 1.1f;        // Gradient radius scale factor for written tracks

    // -----------------------------------------------------------------------
    //  Theme colours — matching the "frozen fire" palette
    // -----------------------------------------------------------------------

    private static readonly SKColor BgColor = SKColor.Parse("#0E1A2B");
    private static readonly SKColor HubColor = SKColor.Parse("#1A2A3D");
    private static readonly SKColor HubRingColor = SKColor.Parse("#2A3F55");
    private static readonly SKColor InnerRingColor = SKColor.Parse("#0F1D2D");
    private static readonly SKColor UnwrittenTrackColor = SKColor.Parse("#141E2E");
    private static readonly SKColor DiscEdgeColor = SKColor.Parse("#1E3450");
    private static readonly SKColor DiscSurfaceColor = SKColor.Parse("#0C1622");
    private static readonly SKColor LabelColor = SKColor.Parse("#8EAFC8");
    private static readonly SKColor LabelDimColor = SKColor.Parse("#5A7A9A");
    private static readonly SKColor BurnGlowStart = SKColor.Parse("#1E90FF");   // Fire blue
    private static readonly SKColor BurnGlowMid = SKColor.Parse("#5BC0FF");     // Light blue
    private static readonly SKColor BurnGlowEnd = SKColor.Parse("#FF8C00");     // Orange (heat)
    private static readonly SKColor VerifyGlowStart = SKColor.Parse("#4CAF50");
    private static readonly SKColor VerifyGlowMid = SKColor.Parse("#81C784");
    private static readonly SKColor VerifyGlowEnd = SKColor.Parse("#1E90FF");
    private static readonly SKColor ReadGlowStart = SKColor.Parse("#FF9800");   // Orange (reading)
    private static readonly SKColor ReadGlowMid = SKColor.Parse("#FFB74D");     // Light orange
    private static readonly SKColor ReadGlowEnd = SKColor.Parse("#FFC107");     // Amber
    private static readonly SKColor ErrorColor = SKColor.Parse("#E04520");
    private static readonly SKColor CompletedColor = SKColor.Parse("#4CAF50");
    private static readonly SKColor WrittenTrackColor = SKColor.Parse("#1E3A5A");

    public DiscVisualizationControl()
    {
        // Reserve space for the visualization.
        MinHeight = 200;
        MinWidth = 200;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.Custom(new DiscDrawOperation(bounds, this));
    }

    /// <summary>
    /// Forces a re-render of the visualization. Call from the UI thread
    /// after updating progress properties.
    /// </summary>
    public void UpdateVisualization()
    {
        InvalidateVisual();
    }

    /// <summary>
    /// Configures the visualization's multi-layer properties based on the media type string.
    /// This is a convenience method that maps known media type names (from OpticalDrive.ProfileToMediaType)
    /// to the correct layer count and track path type.
    ///
    /// Media type to layer mapping (per ECMA-279, ECMA-359, SFF-8090):
    ///   DVD DL variants:  2 layers, OTP (default for recordable/video DL media)
    ///   BD DL:            2 layers, PTP (Blu-ray always uses parallel track path)
    ///   BDXL TL:          3 layers, PTP
    ///   BDXL QL:          4 layers, PTP
    ///   HD DVD DL:        2 layers, OTP
    ///   All others:       1 layer (single-layer, no change)
    /// </summary>
    /// <param name="mediaType">Media type string (e.g. "DVD+R DL", "BD-R DL", "BD-RE TL (BDXL)").</param>
    /// <param name="layerBreakLba">Optional explicit layer break LBA position.</param>
    public void ConfigureForMedia(string? mediaType, long layerBreakLba = 0)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            LayerCount = 1;
            TrackPath = TrackPathType.PTP;
            LayerBreakLba = 0;
            return;
        }

        var mt = mediaType.ToUpperInvariant();

        // Detect layer count and track path from media type string.
        // BDXL quad-layer (QL)
        if (mt.Contains("QL") || mt.Contains("QUAD"))
        {
            LayerCount = 4;
            TrackPath = TrackPathType.PTP; // BD/BDXL always PTP
        }
        // BDXL triple-layer (TL)
        else if (mt.Contains("TL") || mt.Contains("TRIPLE"))
        {
            LayerCount = 3;
            TrackPath = TrackPathType.PTP; // BD/BDXL always PTP
        }
        // Dual-layer detection
        else if (mt.Contains("DL") || mt.Contains("DUAL"))
        {
            LayerCount = 2;

            // Blu-ray dual-layer: always PTP per ECMA-359
            if (mt.Contains("BD"))
            {
                TrackPath = TrackPathType.PTP;
            }
            // DVD-ROM DL can be PTP, but recordable DVD DL and HD DVD DL default to OTP
            else if (mt.Contains("DVD-ROM"))
            {
                // DVD-ROM DL can be either OTP or PTP; default to PTP for data
                TrackPath = TrackPathType.PTP;
            }
            else
            {
                // DVD±R DL, DVD±RW DL, HD DVD DL: default to OTP
                // (most common for video and recordable dual-layer media)
                TrackPath = TrackPathType.OTP;
            }
        }
        else
        {
            LayerCount = 1;
            TrackPath = TrackPathType.PTP;
        }

        LayerBreakLba = layerBreakLba;
    }

    // =======================================================================
    //  ICustomDrawOperation — SkiaSharp rendering
    // =======================================================================

    private sealed class DiscDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly long _currentLba;
        private readonly long _totalSectors;
        private readonly int _percentComplete;
        private readonly double _currentSpeedX;
        private readonly DiscOperationMode _mode;
        private readonly long _badSectors;
        private readonly bool _isActive;
        private readonly bool _isCompleted;
        private readonly bool? _verifyPassed;
        private readonly int _layerCount;
        private readonly TrackPathType _trackPath;
        private readonly long _layerBreakLba;

        public DiscDrawOperation(Rect bounds, DiscVisualizationControl ctrl)
        {
            _bounds = bounds;
            _currentLba = ctrl.CurrentLba;
            _totalSectors = ctrl.TotalSectors;
            _percentComplete = ctrl.PercentComplete;
            _currentSpeedX = ctrl.CurrentSpeedX;
            _mode = ctrl.OperationMode;
            _badSectors = ctrl.BadSectors;
            _isActive = ctrl.IsActive;
            _isCompleted = ctrl.IsCompleted;
            _verifyPassed = ctrl.VerifyPassed;
            _layerCount = Math.Clamp(ctrl.LayerCount, 1, 4);
            _trackPath = ctrl.TrackPath;
            _layerBreakLba = ctrl.LayerBreakLba;
        }

        public Rect Bounds => _bounds;

        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other)
            => other is DiscDrawOperation op
               && op._currentLba == _currentLba
               && op._totalSectors == _totalSectors
               && op._percentComplete == _percentComplete
               && op._isActive == _isActive
               && op._isCompleted == _isCompleted
               && op._layerCount == _layerCount
               && op._trackPath == _trackPath;

        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))
                as ISkiaSharpApiLeaseFeature;
            if (leaseFeature == null)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            RenderDisc(canvas);
        }

        // -------------------------------------------------------------------
        //  Main rendering pipeline
        // -------------------------------------------------------------------

        private void RenderDisc(SKCanvas canvas)
        {
            var width = (float)_bounds.Width;
            var height = (float)_bounds.Height;

            if (width < 1 || height < 1) return;

            canvas.Save();

            // Use the full available area — the disc takes the left portion,
            // and stats appear on the right.
            var discDiameter = Math.Min(width * 0.55f, height - 16);
            if (discDiameter < 60) discDiameter = Math.Min(width, height) - 8;
            var discRadius = discDiameter / 2f;
            var cx = discRadius + 12f;
            var cy = height / 2f;

            // Compute per-layer geometry
            var layers = ComputeLayerGeometry(discRadius);

            // Draw disc background
            DrawDiscBackground(canvas, cx, cy, discRadius);

            // Draw data spirals for each layer (unwritten, then written)
            for (int i = 0; i < layers.Length; i++)
            {
                DrawSpiralTracks(canvas, cx, cy, layers[i], written: false);
            }

            // Draw written/verified area for each layer
            if (_totalSectors > 0 && (_currentLba > 0 || _isCompleted))
            {
                for (int i = 0; i < layers.Length; i++)
                {
                    DrawSpiralTracks(canvas, cx, cy, layers[i], written: true);
                }
            }

            // Draw layer boundary indicators for multi-layer media
            if (layers.Length > 1)
            {
                DrawLayerBoundaries(canvas, cx, cy, discRadius, layers);
            }

            // Draw write/verify head glow
            if (_isActive && _totalSectors > 0 && _currentLba > 0 && !_isCompleted)
            {
                DrawLaserHead(canvas, cx, cy, discRadius, layers);
            }

            // Draw hub and center hole
            DrawHub(canvas, cx, cy, discRadius);

            // Draw outer rim
            DrawOuterRim(canvas, cx, cy, discRadius);

            // Draw stats text on the right side
            var statsX = cx + discRadius + 16f;
            DrawStats(canvas, statsX, cy, width - statsX - 8f, height);

            canvas.Restore();
        }

        // -------------------------------------------------------------------
        //  Disc structure drawing
        // -------------------------------------------------------------------

        private static void DrawDiscBackground(SKCanvas canvas, float cx, float cy, float radius)
        {
            using var bgPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = DiscSurfaceColor
            };
            canvas.DrawCircle(cx, cy, radius * OuterRimRadius, bgPaint);

            // Subtle radial gradient for disc depth effect
            using var gradientPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(cx, cy),
                    radius,
                    new[] { SKColor.Parse("#14202F"), SKColor.Parse("#0A1520") },
                    new float[] { 0.3f, 1.0f },
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawCircle(cx, cy, radius * OuterRimRadius, gradientPaint);
        }

        private static void DrawHub(SKCanvas canvas, float cx, float cy, float radius)
        {
            // Inner stacking ring
            using var ringPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = InnerRingColor
            };
            canvas.DrawCircle(cx, cy, radius * InnerRingRadius, ringPaint);

            // Hub area
            using var hubPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(cx, cy),
                    radius * HubRadius,
                    new[] { HubColor, SKColor.Parse("#0F1A28") },
                    new float[] { 0.4f, 1.0f },
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawCircle(cx, cy, radius * HubRadius, hubPaint);

            // Hub ring edge
            using var hubEdgePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                Color = HubRingColor,
                StrokeWidth = 1.5f
            };
            canvas.DrawCircle(cx, cy, radius * HubRadius, hubEdgePaint);
            canvas.DrawCircle(cx, cy, radius * InnerRingRadius, hubEdgePaint);

            // Center hole
            using var holePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = BgColor
            };
            canvas.DrawCircle(cx, cy, radius * 0.06f, holePaint);
        }

        private static void DrawOuterRim(SKCanvas canvas, float cx, float cy, float radius)
        {
            using var rimPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                Color = DiscEdgeColor,
                StrokeWidth = 1.5f
            };
            canvas.DrawCircle(cx, cy, radius * OuterRimRadius, rimPaint);
        }

        // -------------------------------------------------------------------
        //  Layer geometry computation
        //
        //  For multi-layer media, the total data area [DataStartRadius, DataEndRadius]
        //  is divided among layers, with a small visual gap between them.
        //
        //  Track direction per layer depends on the TrackPathType:
        //    PTP: all layers spiral inner→outer (BD, BDXL, DVD-ROM DL)
        //    OTP: L0 inner→outer, L1 outer→inner (DVD-Video DL, DVD±R DL)
        //
        //  The LBA range is split at the layer break position. If no explicit
        //  break is set, sectors are divided equally among layers.
        // -------------------------------------------------------------------

        /// <summary>
        /// Per-layer rendering parameters for the disc visualization.
        /// </summary>
        private readonly struct LayerInfo
        {
            /// <summary>Inner pixel radius of this layer's data area.</summary>
            public readonly float RStart;
            /// <summary>Outer pixel radius of this layer's data area.</summary>
            public readonly float REnd;
            /// <summary>Number of visual spiral revolutions for this layer.</summary>
            public readonly int Revolutions;
            /// <summary>Starting LBA offset for this layer.</summary>
            public readonly long LbaStart;
            /// <summary>Ending LBA for this layer (exclusive).</summary>
            public readonly long LbaEnd;
            /// <summary>Zero-based layer index.</summary>
            public readonly int LayerIndex;
            /// <summary>True if this layer's spiral goes from outer to inner (OTP Layer 1).</summary>
            public readonly bool Reversed;

            public LayerInfo(float rStart, float rEnd, int revolutions,
                             long lbaStart, long lbaEnd, int layerIndex, bool reversed)
            {
                RStart = rStart;
                REnd = rEnd;
                Revolutions = revolutions;
                LbaStart = lbaStart;
                LbaEnd = lbaEnd;
                LayerIndex = layerIndex;
                Reversed = reversed;
            }
        }

        /// <summary>
        /// Computes the rendering geometry for each layer of the disc.
        /// Single-layer discs return a single LayerInfo spanning the full data area.
        /// Multi-layer discs divide the data area among layers with visual gaps.
        /// </summary>
        private LayerInfo[] ComputeLayerGeometry(float discRadius)
        {
            var rDataStart = discRadius * DataStartRadius;
            var rDataEnd = discRadius * DataEndRadius;

            if (_layerCount <= 1)
            {
                // Single-layer: entire data area as one layer (original behavior)
                return new[]
                {
                    new LayerInfo(rDataStart, rDataEnd, VisualRevolutions,
                                  0, _totalSectors, 0, reversed: false)
                };
            }

            // Multi-layer: divide the radial data area among layers with visual gaps.
            // A small gap (2% of data area per boundary) separates layers visually.
            var totalDataRange = rDataEnd - rDataStart;
            var gapSize = totalDataRange * LayerGapFraction;
            var totalGaps = (_layerCount - 1) * gapSize;
            var usableRange = totalDataRange - totalGaps;
            var layerRadialSpan = usableRange / _layerCount;

            // Compute per-layer revolutions proportionally
            var revsPerLayer = Math.Max(MinRevsPerLayer, VisualRevolutions / _layerCount);

            // Compute LBA splits. Use explicit layer break if available,
            // otherwise split evenly.
            var lbaSplits = ComputeLbaSplits();

            var layers = new LayerInfo[_layerCount];
            for (int i = 0; i < _layerCount; i++)
            {
                var layerRStart = rDataStart + i * (layerRadialSpan + gapSize);
                var layerREnd = layerRStart + layerRadialSpan;

                // Determine spiral direction for this layer.
                // OTP: even-numbered layers (0, 2) go inner→outer,
                //      odd-numbered layers (1, 3) go outer→inner.
                // PTP: all layers go inner→outer.
                var reversed = _trackPath == TrackPathType.OTP && (i % 2 == 1);

                layers[i] = new LayerInfo(
                    layerRStart, layerREnd, revsPerLayer,
                    lbaSplits[i], lbaSplits[i + 1], i, reversed);
            }

            return layers;
        }

        /// <summary>
        /// Computes LBA boundary positions for each layer.
        /// Returns an array of length LayerCount + 1 with cumulative LBA boundaries
        /// (e.g. [0, layerBreak, totalSectors] for a 2-layer disc).
        /// </summary>
        private long[] ComputeLbaSplits()
        {
            var splits = new long[_layerCount + 1];
            splits[0] = 0;
            splits[_layerCount] = _totalSectors;

            if (_layerCount == 2 && _layerBreakLba > 0 && _layerBreakLba < _totalSectors)
            {
                // Explicit layer break position provided
                splits[1] = _layerBreakLba;
            }
            else
            {
                // Distribute sectors evenly across layers
                for (int i = 1; i < _layerCount; i++)
                {
                    splits[i] = _totalSectors * i / _layerCount;
                }
            }

            return splits;
        }

        // -------------------------------------------------------------------
        //  Spiral track drawing — multi-layer aware
        //
        //  An Archimedean spiral in polar coordinates:
        //     r(θ) = r_start + (pitch / 2π) × θ
        //
        //  For the visual spiral with N revolutions over [r_start, r_end]:
        //     pitch = (r_end - r_start) / N
        //     θ_max = N × 2π
        //
        //  The fraction of the spiral covered by a given LBA:
        //     f = currentLba / totalSectors    (for CLV, sectors are evenly
        //         distributed along the arc length)
        //
        //  For accurate CLV mapping, the arc length of the Archimedean spiral
        //  up to angle θ is: s(θ) = ∫₀ᶿ √(r² + (dr/dθ)²) dθ
        //  Since dr/dθ = pitch/(2π) is very small relative to r, this
        //  simplifies to s ≈ ∫₀ᶿ r(θ') dθ', which yields:
        //     s(θ) ≈ r_start·θ + (pitch/(4π))·θ²
        //
        //  For a fraction f of total arc length, we solve the quadratic to
        //  find θ_f. This is the correct CLV sector-to-angle mapping.
        //
        //  For OTP (Opposite Track Path) Layer 1, the spiral is rendered from
        //  outer radius inward. The spiral equation becomes:
        //     r(θ) = r_end - (pitch / 2π) × θ
        //  where θ increases as the radius decreases. This correctly models
        //  the physical reversal of the track on OTP Layer 1.
        // -------------------------------------------------------------------

        private void DrawSpiralTracks(SKCanvas canvas, float cx, float cy,
                                      LayerInfo layer, bool written)
        {
            var rStart = layer.RStart;
            var rEnd = layer.REnd;
            var revolutions = layer.Revolutions;
            var pitch = (rEnd - rStart) / revolutions;
            var thetaMax = revolutions * 2.0 * Math.PI;

            // Compute the fraction of this layer's spiral to draw
            double fractionToDraw;
            if (!written)
            {
                fractionToDraw = 1.0; // Draw entire spiral as unwritten
            }
            else
            {
                var layerSectors = layer.LbaEnd - layer.LbaStart;
                if (layerSectors <= 0)
                {
                    fractionToDraw = _isCompleted ? 1.0 : 0.0;
                }
                else if (_isCompleted)
                {
                    fractionToDraw = 1.0;
                }
                else
                {
                    // How much of this layer has been written?
                    // CurrentLba is a global LBA — map it to this layer's range.
                    var lbaInLayer = _currentLba - layer.LbaStart;
                    if (lbaInLayer <= 0)
                    {
                        fractionToDraw = 0.0;
                    }
                    else if (lbaInLayer >= layerSectors)
                    {
                        fractionToDraw = 1.0;
                    }
                    else
                    {
                        fractionToDraw = (double)lbaInLayer / layerSectors;
                    }
                }
            }

            // Solve for θ at the given arc length fraction
            // For the spiral starting point, use the non-reversed rStart for the
            // arc-length quadratic, since the spiral geometry is the same regardless
            // of direction — only the rendering direction changes.
            var thetaDraw = ArcFractionToTheta(fractionToDraw, rStart, pitch, thetaMax);

            // Determine colors and stroke
            SKColor trackColor;
            float strokeWidth;
            if (!written)
            {
                trackColor = UnwrittenTrackColor;
                strokeWidth = Math.Max(0.6f, pitch * 0.35f);
            }
            else
            {
                trackColor = WrittenTrackColor;
                strokeWidth = Math.Max(0.8f, pitch * 0.45f);
            }

            using var trackPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                StrokeCap = SKStrokeCap.Butt
            };

            if (written)
            {
                // Gradient from inner (blue) to outer based on operation mode
                var colors = _mode switch
                {
                    DiscOperationMode.Burning => new[] { BurnGlowStart, BurnGlowMid, WrittenTrackColor },
                    DiscOperationMode.Reading => new[] { ReadGlowStart, ReadGlowMid, WrittenTrackColor },
                    _ => new[] { VerifyGlowStart, VerifyGlowMid, WrittenTrackColor }
                };
                trackPaint.Shader = SKShader.CreateRadialGradient(
                    new SKPoint(cx, cy),
                    rEnd,
                    colors,
                    new float[] { rStart / (rEnd * GradientRadiusScale), 0.65f, 1.0f },
                    SKShaderTileMode.Clamp);
            }
            else
            {
                trackPaint.Color = trackColor;
            }

            // Build the spiral path using small angle increments
            var path = new SKPath();
            var step = Math.PI / 90.0; // 2° per step for smooth curves
            var maxTheta = written ? thetaDraw : thetaMax;
            var started = false;

            for (double theta = 0; theta <= maxTheta; theta += step)
            {
                var (x, y) = SpiralPoint(cx, cy, rStart, rEnd, pitch, theta, layer.Reversed);

                if (!started)
                {
                    path.MoveTo(x, y);
                    started = true;
                }
                else
                {
                    path.LineTo(x, y);
                }
            }

            // Final point at exact maxTheta
            {
                var (x, y) = SpiralPoint(cx, cy, rStart, rEnd, pitch, maxTheta, layer.Reversed);
                path.LineTo(x, y);
            }

            canvas.DrawPath(path, trackPaint);
            path.Dispose();
        }

        /// <summary>
        /// Computes the (x, y) pixel position on the spiral at a given angle θ.
        /// For non-reversed (PTP / OTP Layer 0): r increases from rStart outward.
        /// For reversed (OTP Layer 1): r decreases from rEnd inward.
        /// </summary>
        private static (float x, float y) SpiralPoint(
            float cx, float cy, float rStart, float rEnd,
            float pitch, double theta, bool reversed)
        {
            double r;
            if (reversed)
            {
                // OTP Layer 1: spiral from outer to inner
                r = rEnd - (pitch / (2.0 * Math.PI)) * theta;
            }
            else
            {
                // Normal: spiral from inner to outer
                r = rStart + (pitch / (2.0 * Math.PI)) * theta;
            }

            var x = cx + (float)(r * Math.Cos(theta));
            var y = cy + (float)(r * Math.Sin(theta));
            return (x, y);
        }

        // -------------------------------------------------------------------
        //  Layer boundary indicators
        //
        //  For multi-layer media, draw a subtle ring at the boundary between
        //  layers. This helps visualize where the layer transition occurs.
        // -------------------------------------------------------------------

        private static void DrawLayerBoundaries(SKCanvas canvas, float cx, float cy,
                                                float discRadius, LayerInfo[] layers)
        {
            using var boundaryPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                Color = SKColor.Parse("#2A4A6A"),
                StrokeWidth = 1.0f,
                PathEffect = SKPathEffect.CreateDash(new float[] { LayerBoundaryDashLength, LayerBoundaryDashGap }, 0)
            };

            for (int i = 0; i < layers.Length - 1; i++)
            {
                // The boundary is between the end of layer i and start of layer i+1.
                // Draw the ring at the midpoint of the gap.
                var gapCenter = (layers[i].REnd + layers[i + 1].RStart) / 2f;
                canvas.DrawCircle(cx, cy, gapCenter, boundaryPaint);
            }
        }

        // -------------------------------------------------------------------
        //  Laser/read head glow — positioned at the current LBA on the
        //  correct layer and spiral point.
        // -------------------------------------------------------------------

        private void DrawLaserHead(SKCanvas canvas, float cx, float cy,
                                   float discRadius, LayerInfo[] layers)
        {
            // Find which layer the current LBA is on
            var activeLayer = layers[0];
            foreach (var layer in layers)
            {
                if (_currentLba >= layer.LbaStart && _currentLba < layer.LbaEnd)
                {
                    activeLayer = layer;
                    break;
                }
                // If past this layer, use the last layer
                activeLayer = layer;
            }

            var rStart = activeLayer.RStart;
            var rEnd = activeLayer.REnd;
            var pitch = (rEnd - rStart) / activeLayer.Revolutions;
            var thetaMax = activeLayer.Revolutions * 2.0 * Math.PI;

            // Compute the fraction within this layer
            var layerSectors = activeLayer.LbaEnd - activeLayer.LbaStart;
            double fraction;
            if (layerSectors <= 0)
            {
                fraction = 0;
            }
            else
            {
                var lbaInLayer = _currentLba - activeLayer.LbaStart;
                fraction = Math.Clamp((double)lbaInLayer / layerSectors, 0.0, 1.0);
            }

            var theta = ArcFractionToTheta(fraction, rStart, pitch, thetaMax);
            var (hx, hy) = SpiralPoint(cx, cy, rStart, rEnd, pitch, theta, activeLayer.Reversed);

            // Outer glow
            var glowRadius = Math.Max(6f, discRadius * 0.04f);
            var glowColor = _mode switch
            {
                DiscOperationMode.Burning => BurnGlowEnd,
                DiscOperationMode.Reading => ReadGlowEnd,
                _ => VerifyGlowEnd
            };
            var glowColorOuter = glowColor.WithAlpha(0);

            using var glowPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(hx, hy),
                    glowRadius * 3f,
                    new[] { glowColor.WithAlpha(100), glowColor.WithAlpha(40), glowColorOuter },
                    new float[] { 0f, 0.4f, 1.0f },
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawCircle(hx, hy, glowRadius * 3f, glowPaint);

            // Core dot
            var coreColor = _mode switch
            {
                DiscOperationMode.Burning => BurnGlowMid,
                DiscOperationMode.Reading => ReadGlowMid,
                _ => VerifyGlowMid
            };
            using var corePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = coreColor
            };
            canvas.DrawCircle(hx, hy, glowRadius * 0.6f, corePaint);

            // Bright center
            using var centerPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = SKColors.White.WithAlpha(200)
            };
            canvas.DrawCircle(hx, hy, glowRadius * 0.25f, centerPaint);
        }

        // -------------------------------------------------------------------
        //  Stats overlay text
        // -------------------------------------------------------------------

        private void DrawStats(SKCanvas canvas, float x, float cy,
                               float availableWidth, float height)
        {
            if (availableWidth < 40) return;

            var fontSize = Math.Clamp(height * 0.065f, 10f, 14f);
            var smallFont = Math.Clamp(height * 0.050f, 8f, 11f);
            var lineSpacing = fontSize * 1.7f;

            using var titleFont = new SKPaint
            {
                IsAntialias = true,
                Color = LabelColor,
                TextSize = fontSize,
                Typeface = SKTypeface.FromFamilyName("Inter",
                    SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
                TextAlign = SKTextAlign.Left
            };

            using var valueFont = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White,
                TextSize = fontSize,
                Typeface = SKTypeface.FromFamilyName("Cascadia Code",
                    SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                    ?? SKTypeface.FromFamilyName("Consolas")
                    ?? SKTypeface.Default,
                TextAlign = SKTextAlign.Left
            };

            using var dimFont = new SKPaint
            {
                IsAntialias = true,
                Color = LabelDimColor,
                TextSize = smallFont,
                Typeface = SKTypeface.FromFamilyName("Inter"),
                TextAlign = SKTextAlign.Left
            };

            // Vertical center — start drawing from top of stats block
            var totalLines = _isActive || _isCompleted
                ? (_layerCount > 1 ? 8 : 7)
                : 3;
            var blockHeight = totalLines * lineSpacing;
            var startY = cy - blockHeight / 2f + fontSize;

            var curY = startY;

            // Mode title
            var modeLabel = _mode switch
            {
                DiscOperationMode.Burning => "BURNING",
                DiscOperationMode.Reading => "READING",
                _ => "VERIFYING"
            };
            if (_isCompleted)
            {
                if (_mode == DiscOperationMode.Verifying)
                    modeLabel = _verifyPassed == true ? "VERIFY PASSED" : "VERIFY FAILED";
                else if (_mode == DiscOperationMode.Reading)
                    modeLabel = "READ COMPLETE";
                else
                    modeLabel = "BURN COMPLETE";
            }
            else if (!_isActive)
            {
                modeLabel = "READY";
            }

            var modeColor = _isCompleted
                ? (_verifyPassed == false || (_mode == DiscOperationMode.Burning && _badSectors > 0)
                    ? ErrorColor : CompletedColor)
                : _mode switch
                {
                    DiscOperationMode.Burning => BurnGlowStart,
                    DiscOperationMode.Reading => ReadGlowStart,
                    _ => VerifyGlowStart
                };

            using var modePaint = new SKPaint
            {
                IsAntialias = true,
                Color = modeColor,
                TextSize = fontSize * 1.1f,
                Typeface = SKTypeface.FromFamilyName("Inter",
                    SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
                TextAlign = SKTextAlign.Left
            };
            canvas.DrawText(modeLabel, x, curY, modePaint);
            curY += lineSpacing * 1.2f;

            if (_isActive || _isCompleted)
            {
                // LBA position
                canvas.DrawText("LBA", x, curY, titleFont);
                canvas.DrawText(FormatLba(_currentLba), x + 50, curY, valueFont);
                curY += lineSpacing;

                // Total sectors
                if (_totalSectors > 0)
                {
                    canvas.DrawText("Total", x, curY, titleFont);
                    canvas.DrawText(FormatLba(_totalSectors), x + 50, curY, valueFont);
                    curY += lineSpacing;
                }

                // Layer info for multi-layer media
                if (_layerCount > 1)
                {
                    canvas.DrawText("Layer", x, curY, titleFont);
                    var currentLayer = GetCurrentLayerIndex(
                        _currentLba, _totalSectors, _layerCount, _layerBreakLba);
                    var pathLabel = _trackPath == TrackPathType.OTP ? "OTP" : "PTP";
                    canvas.DrawText($"L{currentLayer}/{_layerCount} ({pathLabel})",
                        x + 50, curY, valueFont);
                    curY += lineSpacing;
                }

                // Speed
                if (_currentSpeedX > 0)
                {
                    canvas.DrawText("Speed", x, curY, titleFont);
                    canvas.DrawText($"{_currentSpeedX:F1}x", x + 50, curY, valueFont);
                    curY += lineSpacing;
                }

                // Progress percentage
                canvas.DrawText("Done", x, curY, titleFont);
                canvas.DrawText($"{_percentComplete}%", x + 50, curY, valueFont);
                curY += lineSpacing;

                // Bad sectors (if any)
                if (_badSectors > 0)
                {
                    using var errorPaint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = ErrorColor,
                        TextSize = fontSize,
                        Typeface = titleFont.Typeface,
                        TextAlign = SKTextAlign.Left
                    };
                    canvas.DrawText($"Bad: {_badSectors:N0}", x, curY, errorPaint);
                    curY += lineSpacing;
                }

                // Disc position hint
                var positionDesc = GetPositionDescription(
                    _currentLba, _totalSectors, _percentComplete,
                    _layerCount, _trackPath, _layerBreakLba);
                canvas.DrawText(positionDesc, x, curY, dimFont);
            }
            else
            {
                canvas.DrawText("Waiting for operation...", x, curY, dimFont);
            }
        }

        // -------------------------------------------------------------------
        //  LBA-to-Polar coordinate conversion (CLV arc-length model)
        //
        //  For an Archimedean spiral r(θ) = a + bθ with b = pitch/(2π):
        //    Arc length s(θ) ≈ aθ + bθ²/2    (valid when b << a)
        //    Total arc length S = s(θ_max)
        //
        //  Given fraction f = s_target/S, solve aθ + bθ²/2 = f·S for θ:
        //    θ = (-a + √(a² + 2b·f·S)) / b    (quadratic formula)
        // -------------------------------------------------------------------

        private static double ArcFractionToTheta(double fraction, double rStart,
                                                  double pitch, double thetaMax)
        {
            if (fraction <= 0) return 0;
            if (fraction >= 1) return thetaMax;

            var b = pitch / (2.0 * Math.PI); // dr/dθ
            var a = rStart;                   // r at θ = 0

            // Total arc length approximation
            var sTotal = a * thetaMax + b * thetaMax * thetaMax / 2.0;
            var sTarget = fraction * sTotal;

            // Solve: bθ²/2 + aθ - sTarget = 0
            // Discriminant: a² + 2b·sTarget
            var discriminant = a * a + 2.0 * b * sTarget;
            if (discriminant < 0) return 0;

            var theta = (-a + Math.Sqrt(discriminant)) / b;
            return Math.Clamp(theta, 0, thetaMax);
        }

        // -------------------------------------------------------------------
        //  Utility
        // -------------------------------------------------------------------

        private static string FormatLba(long lba)
        {
            if (lba >= 1_000_000)
                return $"{lba / 1_000_000.0:F2}M";
            if (lba >= 1_000)
                return $"{lba / 1_000.0:F1}K";
            return lba.ToString("N0");
        }

        /// <summary>
        /// Returns a human-readable description of the current physical
        /// position on the disc based on the LBA fraction, with layer awareness.
        /// </summary>
        private static string GetPositionDescription(long currentLba, long totalSectors, int pct,
                                                      int layerCount, TrackPathType trackPath,
                                                      long layerBreakLba)
        {
            if (totalSectors <= 0) return $"Sector {currentLba:N0}";

            var fraction = (double)currentLba / totalSectors;

            if (layerCount > 1)
            {
                var layerIndex = GetCurrentLayerIndex(currentLba, totalSectors, layerCount, layerBreakLba);
                var layerFraction = GetLayerFraction(currentLba, totalSectors, layerCount, layerBreakLba, layerIndex);

                // For OTP Layer 1, the physical radius decreases as LBA increases,
                // so the position description should reflect the inward spiral.
                var isReversed = trackPath == TrackPathType.OTP && (layerIndex % 2 == 1);
                var physicalFraction = isReversed ? (1.0 - layerFraction) : layerFraction;

                var posDesc = physicalFraction switch
                {
                    < 0.15 => "Inner area",
                    < 0.40 => "Inner-mid area",
                    < 0.60 => "Mid area",
                    < 0.80 => "Outer-mid area",
                    _ => "Outer area"
                };

                var dirHint = isReversed ? "←in" : "→out";
                var rMm = 24.0 + physicalFraction * (58.0 - 24.0);
                return $"L{layerIndex} {posDesc} {dirHint} (r≈{rMm:F0}mm)";
            }

            // Single-layer description (original behavior)
            var desc = fraction switch
            {
                < 0.01 => "Lead-in area",
                < 0.15 => "Inner data area",
                < 0.40 => "Inner-mid data area",
                < 0.60 => "Mid data area",
                < 0.80 => "Outer-mid data area",
                < 0.95 => "Outer data area",
                _      => "Lead-out area"
            };

            // Approximate physical radius in mm (CD-like geometry for display)
            var rMmSingle = 25.0 + fraction * (58.0 - 25.0);
            return $"{desc} (r≈{rMmSingle:F0}mm)";
        }

        /// <summary>
        /// Determines which layer the current LBA falls on.
        /// Returns 0-based layer index.
        /// </summary>
        private static int GetCurrentLayerIndex(long currentLba, long totalSectors,
                                                 int layerCount, long layerBreakLba)
        {
            if (layerCount <= 1 || totalSectors <= 0) return 0;

            if (layerCount == 2 && layerBreakLba > 0 && layerBreakLba < totalSectors)
            {
                return currentLba < layerBreakLba ? 0 : 1;
            }

            // Even split
            var sectorsPerLayer = totalSectors / layerCount;
            if (sectorsPerLayer <= 0) return 0;
            var index = (int)(currentLba / sectorsPerLayer);
            return Math.Clamp(index, 0, layerCount - 1);
        }

        /// <summary>
        /// Computes the progress fraction within a specific layer (0.0 to 1.0).
        /// </summary>
        private static double GetLayerFraction(long currentLba, long totalSectors,
                                                int layerCount, long layerBreakLba,
                                                int layerIndex)
        {
            long layerStart, layerEnd;

            if (layerCount == 2 && layerBreakLba > 0 && layerBreakLba < totalSectors)
            {
                layerStart = layerIndex == 0 ? 0 : layerBreakLba;
                layerEnd = layerIndex == 0 ? layerBreakLba : totalSectors;
            }
            else
            {
                layerStart = totalSectors * layerIndex / layerCount;
                layerEnd = totalSectors * (layerIndex + 1) / layerCount;
            }

            var layerSectors = layerEnd - layerStart;
            if (layerSectors <= 0) return 0;

            var lbaInLayer = currentLba - layerStart;
            return Math.Clamp((double)lbaInLayer / layerSectors, 0.0, 1.0);
        }
    }
}

/// <summary>Disc operation mode for visualization.</summary>
public enum DiscOperationMode
{
    Burning,
    Verifying,
    Reading
}

/// <summary>
/// Track path type for multi-layer optical media.
///
/// Per ECMA-279 (DVD-ROM) and ECMA-267 (DVD Physical Format Information byte 6, bit 4):
///   - PTP (Parallel Track Path): Both layers spiral from inner to outer radius.
///     Used for DVD-ROM data discs. Each layer has its own lead-in/lead-out.
///   - OTP (Opposite Track Path): Layer 0 spirals inner→outer, Layer 1 spirals
///     outer→inner. Used for DVD-Video for seamless layer transition. The laser
///     switches layers at nearly the same physical radius, minimizing seek time.
///
/// Blu-ray (ECMA-359) and BDXL: All layers use inner→outer spiral direction.
/// HD DVD: Same as DVD — supports both OTP and PTP for dual-layer media.
/// </summary>
public enum TrackPathType
{
    /// <summary>
    /// Parallel Track Path — all layers spiral from inner to outer radius.
    /// Used for DVD-ROM, BD, BDXL, and HD DVD data discs.
    /// </summary>
    PTP,

    /// <summary>
    /// Opposite Track Path — Layer 0 spirals inner→outer, Layer 1 spirals outer→inner.
    /// Used for DVD-Video DL, DVD±R DL, DVD±RW DL, and HD DVD-Video DL.
    /// </summary>
    OTP
}
