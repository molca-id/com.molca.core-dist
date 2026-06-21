using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;

namespace Molca.Editor
{
    /// <summary>
    /// GUI-free query filter for sequence steps, shared by the visualizer tree and the
    /// graph editor. Parses a query string into terms and tests steps against all of them
    /// (logical AND). Supported field operators, each matched as a case-insensitive
    /// substring:
    /// <list type="bullet">
    /// <item><c>ref:</c> — the step's Ref Id.</item>
    /// <item><c>type:</c> — the step's concrete type name (e.g. <c>type:branch</c>).</item>
    /// <item><c>aux:</c> — the type name of any auxiliary on the step.</item>
    /// <item><c>status:</c> — runtime <see cref="StepStatus"/> (active/inactive/completed);
    /// only meaningful in play mode.</item>
    /// </list>
    /// Any term without a recognized operator prefix is free text, matched against the
    /// step's GameObject name OR its type name (the legacy search behavior).
    /// </summary>
    /// <remarks>
    /// Construct once per query string and reuse across many steps. The empty/whitespace
    /// query matches every step. Quote a value to keep spaces, e.g. <c>type:"My Step"</c>.
    /// </remarks>
    public sealed class StepQueryFilter
    {
        private enum Field { Free, Ref, Type, Aux, Status }

        private readonly struct Term
        {
            public Field Field { get; }
            public string Value { get; }
            public Term(Field field, string value)
            {
                Field = field;
                Value = value;
            }
        }

        private readonly List<Term> _terms = new List<Term>();

        /// <summary>The raw query string this filter was built from.</summary>
        public string Query { get; }

        /// <summary>Whether the query has no terms (matches every step).</summary>
        public bool IsEmpty => _terms.Count == 0;

        /// <summary>
        /// Builds a filter from a query string. A null/empty/whitespace query produces an
        /// empty filter that matches everything.
        /// </summary>
        /// <param name="query">The query string (operators + free text).</param>
        public StepQueryFilter(string query)
        {
            Query = query ?? string.Empty;
            Parse(Query);
        }

        /// <summary>
        /// Returns whether <paramref name="step"/> satisfies every term in the query.
        /// A null step never matches; an empty query matches any non-null step.
        /// </summary>
        /// <param name="step">The step to test.</param>
        public bool Matches(Step step)
        {
            if (step == null) return false;
            if (_terms.Count == 0) return true;

            foreach (var term in _terms)
            {
                if (!MatchesTerm(step, term)) return false;
            }
            return true;
        }

        /// <summary>
        /// Returns the subset of <paramref name="steps"/> that satisfy the query, preserving order.
        /// </summary>
        /// <param name="steps">Steps to filter. Null entries are dropped.</param>
        public IEnumerable<Step> Filter(IEnumerable<Step> steps)
        {
            if (steps == null) yield break;
            foreach (var step in steps)
            {
                if (Matches(step)) yield return step;
            }
        }

        private static bool MatchesTerm(Step step, Term term)
        {
            switch (term.Field)
            {
                case Field.Ref:
                    return Contains(step.RefId, term.Value);

                case Field.Type:
                    return Contains(step.GetType().Name, term.Value);

                case Field.Aux:
                    return step.Auxiliaries.Any(a => a != null && Contains(a.GetType().Name, term.Value));

                case Field.Status:
                    // Substring so "status:comp" matches Completed; only meaningful at runtime.
                    return Contains(step.CurrentStatus.ToString(), term.Value);

                default: // Free text: name OR type name (preserves legacy search semantics).
                    return Contains(step.name, term.Value) || Contains(step.GetType().Name, term.Value);
            }
        }

        private static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            if (string.IsNullOrEmpty(haystack)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Parse(string query)
        {
            foreach (var token in Tokenize(query))
            {
                int colon = token.IndexOf(':');
                if (colon > 0)
                {
                    string prefix = token.Substring(0, colon);
                    string value = Unquote(token.Substring(colon + 1));
                    if (TryParseField(prefix, out var field))
                    {
                        // Drop empty operator values (e.g. a bare "ref:") rather than match-all.
                        if (!string.IsNullOrEmpty(value)) _terms.Add(new Term(field, value));
                        continue;
                    }
                }

                string free = Unquote(token);
                if (!string.IsNullOrEmpty(free)) _terms.Add(new Term(Field.Free, free));
            }
        }

        private static bool TryParseField(string prefix, out Field field)
        {
            switch (prefix.ToLowerInvariant())
            {
                case "ref": field = Field.Ref; return true;
                case "type": field = Field.Type; return true;
                case "aux": field = Field.Aux; return true;
                case "status": field = Field.Status; return true;
                default: field = Field.Free; return false;
            }
        }

        /// <summary>
        /// Splits on whitespace while keeping double-quoted spans (including the operator
        /// value, e.g. <c>type:"My Step"</c>) intact.
        /// </summary>
        private static IEnumerable<string> Tokenize(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) yield break;

            int i = 0;
            int n = query.Length;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(query[i])) i++;
                if (i >= n) break;

                int start = i;
                bool inQuotes = false;
                while (i < n && (inQuotes || !char.IsWhiteSpace(query[i])))
                {
                    if (query[i] == '"') inQuotes = !inQuotes;
                    i++;
                }
                yield return query.Substring(start, i - start);
            }
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2);
            }
            return value.Replace("\"", string.Empty);
        }
    }
}
