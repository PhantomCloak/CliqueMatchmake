using System.Globalization;
using Lucene.Net.Analysis.Core;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace Sukhoi;

public sealed class MatchQueryParser : QueryParser
{
    public MatchQueryParser() : base(LuceneVersion.LUCENE_48, "properties", new KeywordAnalyzer())
    {
        LowercaseExpandedTerms = false;
    }

    protected override Query GetFieldQuery(string field, string queryText, bool quoted)
    {
        if (!quoted)
        {
            if (queryText.StartsWith(">=", StringComparison.Ordinal) && TryNum(queryText[2..], out double n))
            {
                return NumericRangeQuery.NewDoubleRange(field, n, null, minInclusive: true, maxInclusive: true);
            }
            if (queryText.StartsWith("<=", StringComparison.Ordinal) && TryNum(queryText[2..], out n))
            {
                return NumericRangeQuery.NewDoubleRange(field, null, n, minInclusive: true, maxInclusive: true);
            }
            if (queryText.StartsWith(">", StringComparison.Ordinal) && TryNum(queryText[1..], out n))
            {
                return NumericRangeQuery.NewDoubleRange(field, n, null, minInclusive: false, maxInclusive: true);
            }
            if (queryText.StartsWith("<", StringComparison.Ordinal) && TryNum(queryText[1..], out n))
            {
                return NumericRangeQuery.NewDoubleRange(field, null, n, minInclusive: true, maxInclusive: false);
            }
            if (TryNum(queryText, out n))
            {
                return NumericRangeQuery.NewDoubleRange(field, n, n, minInclusive: true, maxInclusive: true);
            }
        }
        return base.GetFieldQuery(field, queryText, quoted);
    }


    protected override Query GetRangeQuery(string field, string part1, string part2, bool startInclusive, bool endInclusive)
    {
        double? lo = part1 is null or "*" ? null : TryNum(part1, out double a) ? a : null;
        double? hi = part2 is null or "*" ? null : TryNum(part2, out double b) ? b : null;
        if (lo.HasValue || hi.HasValue)
        {
            return NumericRangeQuery.NewDoubleRange(field, lo, hi, startInclusive, endInclusive);
        }
        return base.GetRangeQuery(field, part1, part2, startInclusive, endInclusive);
    }


    protected override Query GetBooleanQuery(IList<BooleanClause> clauses, bool disableCoord)
    {
        Query query = base.GetBooleanQuery(clauses, disableCoord);

        if (query is not BooleanQuery boolean || !IsUnboostedOrGroup(boolean))
        {
            return query;
        }

        return new ConstantScoreQuery(boolean);
    }

    static bool IsUnboostedOrGroup(BooleanQuery boolean) => boolean.Clauses.Count > 0 && boolean.Clauses.All(clause => clause.Occur == Occur.SHOULD && clause.Query.Boost == 1.0f);

    static bool TryNum(string s, out double d) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    public static Query ParseQueryString(string query)
    {
        if (query == "*")
        {
            return new MatchAllDocsQuery();
        }
        try
        {
            return new MatchQueryParser().Parse(query);
        }
        catch (ParseException e)
        {
            throw new ArgumentException(e.Message);
        }
    }

    public static (string[] Strings, Query[] Parsed) ParseLadder(IReadOnlyList<string> queries)
    {
        var strings = new string[queries.Count];
        var parsed = new Query[queries.Count];
        SortedSet<string>? shared = null;

        for (int rung = 0; rung < queries.Count; rung++)
        {
            string text = string.IsNullOrEmpty(queries[rung]) ? "*" : queries[rung];
            Query query = ParseQueryString(text);

            strings[rung] = text;
            parsed[rung] = query;

            if (queries.Count == 1 || query is MatchAllDocsQuery)
            {
                continue;
            }

            SortedSet<string> fields = Fields(query);
            if (shared == null)
            {
                shared = fields;
            }
            else if (!shared.SetEquals(fields))
            {
                throw new MatchmakerException(MatchmakerException.QueryPropertiesDiffer);
            }
        }

        return (strings, parsed);
    }

    static SortedSet<string> Fields(Query query)
    {
        var fields = new SortedSet<string>(StringComparer.Ordinal);
        Collect(query, fields);
        return fields;
    }

    static void Collect(Query query, SortedSet<string> fields)
    {
        switch (query)
        {
            case BooleanQuery boolean:
                foreach (BooleanClause clause in boolean.Clauses)
                {
                    Collect(clause.Query, fields);
                }
                break;
            case ConstantScoreQuery constant when constant.Query is not null:   // the OR-group wrapper
                Collect(constant.Query, fields);
                break;
            case TermQuery term:
                fields.Add(term.Term.Field);
                break;
            case MultiTermQuery multiTerm:   // numeric/term range, prefix, wildcard, regexp, fuzzy
                fields.Add(multiTerm.Field);
                break;
            case PhraseQuery phrase when phrase.GetTerms().Length > 0:
                fields.Add(phrase.GetTerms()[0].Field);
                break;
        }
    }
}
