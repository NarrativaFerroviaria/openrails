// ORTS Flexible Connections V5
// Purpose: Immutable Bellows profile data and strict, profile-local STF validation.
// Geometry belongs to the publisher; a profile without geometry is receiver-only.
using System;
using System.Collections.Generic;
using System.Globalization;
using Orts.Parsers.Msts;

namespace Orts.Simulation.RollingStocks
{
    public sealed class FlexibleConnectionBellowConfig
    {
        public readonly float OuterWidthM, OuterHeightM, InnerWidthM, InnerHeightM, CornerRadiusM;
        public readonly int CornerSegments, Folds;
        public readonly string TextureName;
        public readonly bool CanPublish;

        internal FlexibleConnectionBellowConfig() { }
        internal FlexibleConnectionBellowConfig(float[] sizes, int segments, int folds, string texture)
        {
            OuterWidthM = sizes[0]; OuterHeightM = sizes[1];
            InnerWidthM = sizes[2]; InnerHeightM = sizes[3]; CornerRadiusM = sizes[4];
            CornerSegments = segments; Folds = folds; TextureName = texture; CanPublish = true;
        }
    }

    internal sealed class FlexibleConnectionBellowParser
    {
        readonly HashSet<string> Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool Declared { get { return Seen.Count != 0; } }
        bool Invalid, Active;
        FlexibleConnectionBellowConfig Geometry;

        internal void Read(STFReader stf, string token, string profile)
        {
            bool unique = Seen.Add(token);
            List<string> items;
            // Shared balanced block reader does not change STF structural-error semantics.
            bool valid = FlexibleConnectionHookParser.ReadBlock(stf, out items);
            if (token == "IsBellow")
            {
                int value;
                valid &= items.Count == 1 && int.TryParse(items[0], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out value) && value == 1;
                Active = valid;
            }
            else
            {
                var sizes = new float[5];
                int segments = 0, folds = 0;
                valid &= items.Count == 8;
                if (items.Count == 8)
                {
                    for (int i = 0; i < sizes.Length; i++)
                        valid &= float.TryParse(items[i], NumberStyles.Float, CultureInfo.InvariantCulture, out sizes[i]) &&
                            !float.IsNaN(sizes[i]) && !float.IsInfinity(sizes[i]);
                    valid &= int.TryParse(items[5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out segments) && segments >= 1 && segments <= 16;
                    valid &= int.TryParse(items[6], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out folds) && folds >= 1 && folds <= 64;
                    valid &= sizes[0] > 0 && sizes[1] > 0 && sizes[2] > 0 && sizes[2] < sizes[0] &&
                        sizes[3] > 0 && sizes[3] < sizes[1] && sizes[4] >= 0 && sizes[4] <= Math.Min(sizes[0], sizes[1]) / 2;
                    // An empty quoted name is present syntax; the material loader supplies fallback.
                    if (valid) Geometry = new FlexibleConnectionBellowConfig(sizes, segments, folds, items[7]);
                }
            }
            if (!unique || !valid)
            {
                Invalid = true;
                STFException.TraceWarning(stf, "Bellows Profile '" + profile + "': duplicate or invalid " + token + ". Profile rejected. BellowGeometry needs Ow Oh Iw Ih Crad Cseg F Texture (metres; Cseg 1..16; F 1..64).");
            }
        }

        internal FlexibleConnectionBellowConfig Resolve(STFReader stf, string profile, bool otherProperties)
        {
            if (Invalid || !Active || otherProperties)
            {
                STFException.TraceWarning(stf, "Bellows Profile '" + profile + "': invalid activation or incompatible family properties. Profile rejected.");
                return null;
            }
            return Geometry ?? new FlexibleConnectionBellowConfig();
        }
    }
}
