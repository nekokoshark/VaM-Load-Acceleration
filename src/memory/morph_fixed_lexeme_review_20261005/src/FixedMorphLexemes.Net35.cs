using System;
using System.Text;

namespace VaM.MorphFixedLexemes
{
    // Fixed vocabulary only. Never stores package data, JSON nodes, morphs,
    // builders, source text, numeric values, or arbitrary target names.
    // The 19 literals are a bounded change in string reference identity.
    public static class FixedMorphLexemes
    {
        private static bool Equal(StringBuilder value, string literal)
        {
            if (value.Length != literal.Length) return false;
            for (int i = 0; i < literal.Length; i++)
                if (value[i] != literal[i]) return false;
            return true;
        }

        // category: 1 = one of the three field names, 2 = formula enum text.
        // Matching is ordinal on already-decoded UTF-16 code units. No Trim,
        // culture conversion, normalization, hash allocation, or String.Intern.
        public static bool TryGet(StringBuilder value, out string literal,
                                  out byte category)
        {
            literal = null;
            category = 0;
            if (value == null || value.Length < 3 || value.Length > 12)
                return false;

            char first = value[0];
            switch (first)
            {
                case 't':
                    if (Equal(value, "target")) literal = "target";
                    else if (Equal(value, "targetType")) literal = "targetType";
                    category = 1;
                    break;
                case 'm':
                    if (Equal(value, "multiplier")) literal = "multiplier";
                    category = 1;
                    break;
                case 'M':
                    if (Equal(value, "MorphValue")) literal = "MorphValue";
                    else if (Equal(value, "MCM")) literal = "MCM";
                    else if (Equal(value, "MCMMult")) literal = "MCMMult";
                    category = 2;
                    break;
                case 'B':
                    if (Equal(value, "BoneCenterX")) literal = "BoneCenterX";
                    else if (Equal(value, "BoneCenterY")) literal = "BoneCenterY";
                    else if (Equal(value, "BoneCenterZ")) literal = "BoneCenterZ";
                    category = 2;
                    break;
                case 'O':
                    if (Equal(value, "OrientationX")) literal = "OrientationX";
                    else if (Equal(value, "OrientationY")) literal = "OrientationY";
                    else if (Equal(value, "OrientationZ")) literal = "OrientationZ";
                    category = 2;
                    break;
                case 'G':
                    if (Equal(value, "GeneralScale")) literal = "GeneralScale";
                    category = 2;
                    break;
                case 'S':
                    if (Equal(value, "ScaleX")) literal = "ScaleX";
                    else if (Equal(value, "ScaleY")) literal = "ScaleY";
                    else if (Equal(value, "ScaleZ")) literal = "ScaleZ";
                    category = 2;
                    break;
                case 'R':
                    if (Equal(value, "RotationX")) literal = "RotationX";
                    else if (Equal(value, "RotationY")) literal = "RotationY";
                    else if (Equal(value, "RotationZ")) literal = "RotationZ";
                    category = 2;
                    break;
            }
            if (literal != null) return true;
            category = 0;
            return false;
        }
    }
}
