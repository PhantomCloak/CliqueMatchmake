using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace Sukhoi;

sealed class TicketDocBitSetCollector : ICollector
{
    int _docBase;
    public FixedBitSet Bits { get; }
    public TicketDocBitSetCollector(int maxDoc) => Bits = new FixedBitSet(Math.Max(maxDoc, 1));
    public bool AcceptsDocsOutOfOrder => true;
    public void SetScorer(Scorer scorer) { }
    public void SetNextReader(AtomicReaderContext context) => _docBase = context.DocBase;
    public void Collect(int doc) => Bits.Set(_docBase + doc);
}

sealed class ConsumedFilter : Filter
{
    readonly FixedBitSet[] _sets;
    public ConsumedFilter(FixedBitSet[] sets) => _sets = sets;
    public override DocIdSet GetDocIdSet(AtomicReaderContext context, IBits acceptDocs) => BitsFilteredDocIdSet.Wrap(_sets[context.Ord], acceptDocs);
}

public sealed class SearcherScope : IDisposable
{
    private IndexSearcher _searcher { get; }

    readonly DirectoryReader _reader;
    static readonly Sort RankSort = new(SortField.FIELD_SCORE, new SortField("created_at", SortFieldType.INT64));

    readonly Dictionary<string, FixedBitSet> _queryDocCache = new();
    readonly Dictionary<(int MinCount, int MaxCount), FixedBitSet> _countDocCache = new();
    readonly Dictionary<string, bool> _isQueryScoreFlatCache = new();

    SortedDocValues? _ticketValues;
    bool _ticketValuesLoaded;

    Dictionary<string, int>? _ticketDocs;
    string[]? _flattanedDocIdToTicket;
    int[]? _sortedDocIdList;

    ConsumedFilter? _consumedFilter;
    FixedBitSet[]? _consumedDocs;
    FixedBitSet? _consumedDocsGlobal;

    internal SearcherScope(IndexSearcher searcher, DirectoryReader reader)
    {
        _searcher = searcher;
        _reader = reader;
    }

    private SortedDocValues? TicketList
    {
        get
        {
            if (!_ticketValuesLoaded)
            {
                _ticketValues = MultiDocValues.GetSortedValues(_searcher.IndexReader, "ticket");
                _ticketValuesLoaded = true;
            }
            return _ticketValues;
        }
    }

    private Dictionary<string, int> TicketIdToDocId
    {
        get
        {
            if (_ticketDocs != null)
            {
                return _ticketDocs;
            }

            IndexReader reader = _searcher.IndexReader;
            var docs = new Dictionary<string, int>(reader.NumDocs);
            var byDoc = new string[Math.Max(reader.MaxDoc, 1)];
            IBits? liveDocs = MultiFields.GetLiveDocs(reader);
            SortedDocValues? values = TicketList;

            var docTicketNameByteRef = new BytesRef();
            for (int doc = 0; doc < reader.MaxDoc; doc++)
            {
                if (liveDocs != null && !liveDocs.Get(doc))
                {
                    continue;
                }

                if (values == null)
                {
                    throw new Exception("Shouldn't be happen (TODO: Remove)");
                }

                values.Get(doc, docTicketNameByteRef);
                string ticket = docTicketNameByteRef.Utf8ToString();
                docs[ticket] = doc;
                byDoc[doc] = ticket;
            }

            _ticketDocs = docs;
            _flattanedDocIdToTicket = byDoc;
            return docs;
        }
    }

    private string[] FlattanedDocIdToTicket
    {
        get
        {
            _ = TicketIdToDocId;
            return _flattanedDocIdToTicket!;
        }
    }

    private FixedBitSet[] ConsumedDocs
    {
        get
        {
            if (_consumedDocs != null)
            {
                return _consumedDocs;
            }

            var sets = new FixedBitSet[_reader.Leaves.Count];
            foreach (AtomicReaderContext segment in _reader.Leaves)
            {
                int maxDocInSegment = segment.AtomicReader.MaxDoc;
                var set = new FixedBitSet(Math.Max(maxDocInSegment, 1));
                if (maxDocInSegment > 0)
                {
                    set.Set(startIndex: 0, endIndex: maxDocInSegment);
                }
                sets[segment.Ord] = set;
            }

            _consumedDocs = sets;
            return sets;
        }
    }

    private FixedBitSet ConsumedDocsGlobal
    {
        get
        {
            if (_consumedDocsGlobal != null)
            {
                return _consumedDocsGlobal;
            }

            int maxDoc = _reader.MaxDoc;
            var set = new FixedBitSet(Math.Max(maxDoc, 1));
            if (maxDoc > 0)
            {
                set.Set(startIndex: 0, endIndex: maxDoc);
            }

            _consumedDocsGlobal = set;
            return set;
        }
    }

    private int[] SortedDocIdList
    {
        get
        {
            if (_sortedDocIdList != null)
            {
                return _sortedDocIdList;
            }

            IndexReader reader = _searcher.IndexReader;
            NumericDocValues createdAt = MultiDocValues.GetNumericValues(reader, "created_at") ?? throw new InvalidOperationException("created_at doc values missing");
            IBits? liveDocs = MultiFields.GetLiveDocs(reader);

            var docs = new List<int>(reader.NumDocs);
            for (int doc = 0; doc < reader.MaxDoc; doc++)
            {
                if (liveDocs == null || liveDocs.Get(doc))
                {
                    docs.Add(doc);
                }
            }

            int[] ordered = docs.ToArray();
            Array.Sort(ordered.Select(createdAt.Get).ToArray(), ordered);

            _sortedDocIdList = ordered;
            return ordered;
        }
    }

    public IEnumerable<string> SearchTickets(MatchmakerTicket seed)
    {
        if (!IsQueryScoreFlat(seed.Query, seed.QueryString))
        {
            return SearchTicketsDefault(seed);
        }

        FixedBitSet counts = GetOrCacheMinMaxPair(seed.MinCount, seed.MaxCount);
        FixedBitSet matching = GetOrCacheQueryDocs(seed.Query, seed.QueryString);

        return WalkPool(matching, counts);
    }

    public bool IsAcceptsTo(MatchmakerTicket who, MatchmakerTicket to)
    {
        if (!TicketIdToDocId.TryGetValue(to.Ticket, out int docId))
        {
            return false;
        }

        return GetOrCacheQueryDocs(who.Query, who.QueryString).Get(docId);
    }

    public void Consume(string ticket)
    {
        if (!TicketIdToDocId.TryGetValue(ticket, out int docId))
        {
            return;
        }

        AtomicReaderContext segment = _reader.Leaves[ReaderUtil.SubIndex(docId, _reader.Leaves)];
        ConsumedDocs[segment.Ord].Clear(docId - segment.DocBase);
        ConsumedDocsGlobal.Clear(docId);
    }

    private IEnumerable<string> WalkPool(FixedBitSet matching, FixedBitSet counts)
    {
        int[] sortedDocs = SortedDocIdList;
        string[] docIdToTicket = FlattanedDocIdToTicket;
        FixedBitSet consumed = ConsumedDocsGlobal;

        foreach (int docId in sortedDocs)
        {
            if (!matching.Get(docId) || !counts.Get(docId) || !consumed.Get(docId))
            {
                continue;
            }

            yield return docIdToTicket[docId];
        }
    }

    private List<string> SearchTicketsDefault(MatchmakerTicket seed)
    {
        var query = new BooleanQuery
        {
            { seed.Query, Occur.MUST },
            { NumericRangeQuery.NewDoubleRange("min_count", null, seed.MaxCount, minInclusive: true, maxInclusive: true), Occur.MUST },
            { NumericRangeQuery.NewDoubleRange("max_count", seed.MinCount, null, minInclusive: true, maxInclusive: true), Occur.MUST },
        };

        _consumedFilter ??= new ConsumedFilter(ConsumedDocs);
        var collector = TopFieldCollector.Create(RankSort, Math.Max(_searcher.IndexReader.NumDocs, 1), fillFields: false, trackDocScores: false, trackMaxScore: false, docsScoredInOrder: false);

        _searcher.Search(query, _consumedFilter, collector);
        TopDocs topDocs = collector.GetTopDocs();

        if (topDocs.ScoreDocs.Length == 0)
        {
            return new();
        }

        SortedDocValues values = TicketList ?? throw new InvalidOperationException("ticket doc values missing on a reader with hits");

        var docTicketNameByteRef = new BytesRef();
        var tickets = new List<string>(topDocs.ScoreDocs.Length);
        foreach (ScoreDoc scoreDoc in topDocs.ScoreDocs)
        {
            values.Get(scoreDoc.Doc, docTicketNameByteRef);
            tickets.Add(docTicketNameByteRef.Utf8ToString());
        }

        return tickets;

    }

    private FixedBitSet GetOrCacheMinMaxPair(int minCount, int maxCount)
    {
        if (_countDocCache.TryGetValue((minCount, maxCount), out FixedBitSet? docs))
        {
            return docs;
        }

        var query = new BooleanQuery
        {
            { NumericRangeQuery.NewDoubleRange("min_count", null, maxCount, minInclusive: true, maxInclusive: true), Occur.MUST },
            { NumericRangeQuery.NewDoubleRange("max_count", minCount, null, minInclusive: true, maxInclusive: true), Occur.MUST },
        };

        var collector = new TicketDocBitSetCollector(_searcher.IndexReader.MaxDoc);
        _searcher.Search(query, collector);

        _countDocCache[(minCount, maxCount)] = collector.Bits;
        return collector.Bits;
    }

    private FixedBitSet GetOrCacheQueryDocs(Query query, string queryString)
    {
        if (_queryDocCache.TryGetValue(queryString, out FixedBitSet? docs))
        {
            return docs;
        }

        var collector = new TicketDocBitSetCollector(_searcher.IndexReader.MaxDoc);
        _searcher.Search(query, collector);

        _queryDocCache[queryString] = collector.Bits;
        return collector.Bits;
    }

    private bool IsQueryScoreFlat(Query query, string queryString)
    {
        if (_isQueryScoreFlatCache.TryGetValue(queryString, out bool result))
        {
            return result;
        }

        result = IsFlatScoring(query);
        _isQueryScoreFlatCache[queryString] = result;
        return result;
    }

    static bool IsFlatScoring(Query query) => query switch
    {
        ConstantScoreQuery or MatchAllDocsQuery or TermQuery => true,

        MultiTermQuery multiTerm =>
            multiTerm.MultiTermRewriteMethod == MultiTermQuery.CONSTANT_SCORE_FILTER_REWRITE ||
            multiTerm.MultiTermRewriteMethod == MultiTermQuery.CONSTANT_SCORE_AUTO_REWRITE_DEFAULT ||
            multiTerm.MultiTermRewriteMethod == MultiTermQuery.CONSTANT_SCORE_BOOLEAN_QUERY_REWRITE,

        BooleanQuery boolean =>
            boolean.Clauses.All(clause => clause.Occur != Occur.SHOULD) &&
            boolean.Clauses.Where(clause => clause.Occur == Occur.MUST).All(clause => IsFlatScoring(clause.Query)),

        _ => false,
    };

    public void Dispose() => _reader.Dispose();
}

