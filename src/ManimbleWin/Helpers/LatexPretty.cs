using System;
using System.Collections.Generic;
using System.Text;

namespace ManimbleWin.Helpers
{
    /// <summary>
    /// LaTeX → Unicode 的近似排版。仅在 LaTeX 环境不可用、或尚未渲染完成时作为占位显示，
    /// 让公式在画布上仍然“像公式”，而不是一堆反斜杠命令。
    /// </summary>
    public static class LatexPretty
    {
        private static readonly Dictionary<string, string> Commands = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["\\alpha"] = "α", ["\\beta"] = "β", ["\\gamma"] = "γ", ["\\delta"] = "δ",
            ["\\epsilon"] = "ε", ["\\varepsilon"] = "ε", ["\\zeta"] = "ζ", ["\\eta"] = "η",
            ["\\theta"] = "θ", ["\\vartheta"] = "ϑ", ["\\iota"] = "ι", ["\\kappa"] = "κ",
            ["\\lambda"] = "λ", ["\\mu"] = "μ", ["\\nu"] = "ν", ["\\xi"] = "ξ", ["\\pi"] = "π",
            ["\\rho"] = "ρ", ["\\sigma"] = "σ", ["\\tau"] = "τ", ["\\upsilon"] = "υ",
            ["\\phi"] = "φ", ["\\varphi"] = "φ", ["\\chi"] = "χ", ["\\psi"] = "ψ", ["\\omega"] = "ω",
            ["\\Gamma"] = "Γ", ["\\Delta"] = "Δ", ["\\Theta"] = "Θ", ["\\Lambda"] = "Λ",
            ["\\Xi"] = "Ξ", ["\\Pi"] = "Π", ["\\Sigma"] = "Σ", ["\\Phi"] = "Φ",
            ["\\Psi"] = "Ψ", ["\\Omega"] = "Ω",
            ["\\times"] = "×", ["\\div"] = "÷", ["\\cdot"] = "·", ["\\pm"] = "±", ["\\mp"] = "∓",
            ["\\leq"] = "≤", ["\\le"] = "≤", ["\\geq"] = "≥", ["\\ge"] = "≥", ["\\neq"] = "≠",
            ["\\approx"] = "≈", ["\\equiv"] = "≡", ["\\sim"] = "∼", ["\\propto"] = "∝",
            ["\\infty"] = "∞", ["\\partial"] = "∂", ["\\nabla"] = "∇", ["\\emptyset"] = "∅",
            ["\\sum"] = "∑", ["\\prod"] = "∏", ["\\int"] = "∫", ["\\oint"] = "∮",
            ["\\rightarrow"] = "→", ["\\to"] = "→", ["\\leftarrow"] = "←", ["\\Rightarrow"] = "⇒",
            ["\\Leftarrow"] = "⇐", ["\\leftrightarrow"] = "↔", ["\\mapsto"] = "↦",
            ["\\in"] = "∈", ["\\notin"] = "∉", ["\\subset"] = "⊂", ["\\subseteq"] = "⊆",
            ["\\cup"] = "∪", ["\\cap"] = "∩", ["\\forall"] = "∀", ["\\exists"] = "∃",
            ["\\cdots"] = "⋯", ["\\ldots"] = "…", ["\\dots"] = "…", ["\\vdots"] = "⋮",
            ["\\angle"] = "∠", ["\\perp"] = "⊥", ["\\parallel"] = "∥", ["\\degree"] = "°",
            ["\\circ"] = "∘", ["\\star"] = "⋆", ["\\ast"] = "∗", ["\\hbar"] = "ℏ",
            ["\\ell"] = "ℓ", ["\\Re"] = "ℜ", ["\\Im"] = "ℑ", ["\\prime"] = "′",
            ["\\sin"] = "sin", ["\\cos"] = "cos", ["\\tan"] = "tan", ["\\cot"] = "cot",
            ["\\sec"] = "sec", ["\\csc"] = "csc", ["\\arcsin"] = "arcsin", ["\\arccos"] = "arccos",
            ["\\arctan"] = "arctan", ["\\log"] = "log", ["\\ln"] = "ln", ["\\exp"] = "exp",
            ["\\lim"] = "lim", ["\\max"] = "max", ["\\min"] = "min", ["\\sup"] = "sup",
            ["\\inf"] = "inf", ["\\det"] = "det", ["\\dim"] = "dim", ["\\ker"] = "ker",
            ["\\text"] = "", ["\\mathrm"] = "", ["\\mathbf"] = "", ["\\mathbb"] = "",
            ["\\mathcal"] = "", ["\\operatorname"] = "", ["\\left"] = "", ["\\right"] = "",
            ["\\displaystyle"] = "", ["\\quad"] = "  ", ["\\qquad"] = "   ",
            ["\\,"] = " ", ["\\;"] = " ", ["\\:"] = " ", ["\\!"] = "", ["\\ "] = " ",
            ["\\\\"] = " ", ["\\{"] = "{", ["\\}"] = "}", ["\\%"] = "%", ["\\$"] = "$",
        };

        private static readonly Dictionary<char, char> Superscripts = new Dictionary<char, char>
        {
            ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴', ['5'] = '⁵',
            ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹', ['+'] = '⁺', ['-'] = '⁻',
            ['='] = '⁼', ['('] = '⁽', [')'] = '⁾', ['n'] = 'ⁿ', ['i'] = 'ⁱ', ['a'] = 'ᵃ',
            ['b'] = 'ᵇ', ['c'] = 'ᶜ', ['d'] = 'ᵈ', ['e'] = 'ᵉ', ['x'] = 'ˣ', ['y'] = 'ʸ',
            ['k'] = 'ᵏ', ['m'] = 'ᵐ', ['p'] = 'ᵖ', ['t'] = 'ᵗ', ['T'] = 'ᵀ',
        };

        private static readonly Dictionary<char, char> Subscripts = new Dictionary<char, char>
        {
            ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄', ['5'] = '₅',
            ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉', ['+'] = '₊', ['-'] = '₋',
            ['='] = '₌', ['('] = '₍', [')'] = '₎', ['a'] = 'ₐ', ['e'] = 'ₑ', ['i'] = 'ᵢ',
            ['j'] = 'ⱼ', ['n'] = 'ₙ', ['x'] = 'ₓ', ['k'] = 'ₖ', ['m'] = 'ₘ', ['p'] = 'ₚ',
            ['t'] = 'ₜ', ['s'] = 'ₛ',
        };

        public static string Pretty(string tex)
        {
            if (string.IsNullOrWhiteSpace(tex)) return "";
            string s = tex.Trim();

            s = ReplaceFractions(s);
            s = ReplaceRoots(s);
            s = ReplaceScripts(s, "^{", '^', Superscripts);
            s = ReplaceScripts(s, "_{", '_', Subscripts);

            foreach (var kv in Commands)
                s = s.Replace(kv.Key, kv.Value);

            // 去掉残留在数学模式里的修饰
            s = s.Replace("{", "").Replace("}", "").Replace("$", "").Replace("&", " ");
            return s.Trim();
        }

        private static string ReplaceFractions(string s)
        {
            // \frac{a}{b} / \dfrac{a}{b} / \tfrac{a}{b} → (a)/(b)
            foreach (var cmd in new[] { "\\frac", "\\dfrac", "\\tfrac" })
            {
                int guard = 0;
                int at;
                while ((at = s.IndexOf(cmd, StringComparison.Ordinal)) >= 0 && guard++ < 32)
                {
                    int p = at + cmd.Length;
                    if (!TryReadGroup(s, p, out string a, out int next)) break;
                    if (!TryReadGroup(s, next, out string b, out int after)) break;
                    s = s.Substring(0, at) + "(" + a + ")/(" + b + ")" + s.Substring(after);
                }
            }
            return s;
        }

        private static string ReplaceRoots(string s)
        {
            int guard = 0;
            int at;
            while ((at = s.IndexOf("\\sqrt", StringComparison.Ordinal)) >= 0 && guard++ < 32)
            {
                int p = at + 5;
                if (p < s.Length && s[p] == '[')
                {
                    int close = s.IndexOf(']', p);
                    if (close > 0) p = close + 1;
                }
                if (!TryReadGroup(s, p, out string inner, out int after)) break;
                s = s.Substring(0, at) + "√(" + inner + ")" + s.Substring(after);
            }
            return s;
        }

        private static string ReplaceScripts(string s, string open, char marker, Dictionary<char, char> table)
        {
            int guard = 0;
            int at;
            while ((at = s.IndexOf(open, StringComparison.Ordinal)) >= 0 && guard++ < 64)
            {
                int start = at + open.Length;
                int depth = 1, i = start;
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '{') depth++;
                    else if (s[i] == '}') depth--;
                    if (depth == 0) break;
                    i++;
                }
                if (depth != 0) break;
                string inner = s.Substring(start, i - start);
                s = s.Substring(0, at) + MapScript(inner, marker, table) + s.Substring(Math.Min(s.Length, i + 1));
            }
            return s;
        }

        private static string MapScript(string inner, char marker, Dictionary<char, char> table)
        {
            var sb = new StringBuilder();
            bool all = inner.Length > 0;
            foreach (char c in inner)
            {
                if (table.TryGetValue(c, out char mapped)) sb.Append(mapped);
                else { all = false; break; }
            }
            if (all) return sb.ToString();
            return marker + "(" + inner + ")";
        }

        /// <summary>读取紧跟在 p 位置的 {…} 分组（也接受单个字符）。</summary>
        private static bool TryReadGroup(string s, int p, out string content, out int after)
        {
            content = null; after = p;
            if (p >= s.Length) return false;
            if (s[p] == '{')
            {
                int depth = 1, i = p + 1;
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '{') depth++;
                    else if (s[i] == '}') depth--;
                    if (depth == 0) break;
                    i++;
                }
                if (depth != 0) return false;
                content = s.Substring(p + 1, i - p - 1);
                after = i + 1;
                return true;
            }
            content = s[p].ToString();
            after = p + 1;
            return true;
        }
    }
}
