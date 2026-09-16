using Lucene.Net.Analysis.Core;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Sukhoi;

public class MatchIndex : IDisposable
{
    private sealed class ConstantSimilarity : DefaultSimilarity
    {
        public override float Tf(float freq) => 1.0f;
        public override float Idf(long docFreq, long numDocs) => 1.0f;
        public override float Coord(int overlap, int maxOverlap) => 1.0f;
        public override float QueryNorm(float sumOfSquaredWeights) => 1.0f;
        public override float LengthNorm(FieldInvertState state) => 1.0f;
        public override float SloppyFreq(int distance) => 1.0f;
    };

    ConstantSimilarity defaultSimilarity = new();

    readonly RAMDirectory _directory = new RAMDirectory();
    readonly IndexWriter _writer;

    public MatchIndex()
    {
        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, new KeywordAnalyzer())
        {
            Similarity = defaultSimilarity,
        };
        _writer = new IndexWriter(_directory, config);
    }

    public SearcherScope OpenSearcher()
    {
        var reader = DirectoryReader.Open(_writer, applyAllDeletes: true);
        return new SearcherScope(new IndexSearcher(reader) { Similarity = defaultSimilarity }, reader);
    }

    public void Upsert(MatchmakerTicket index)
    {
        var doc = new Document
        {
            new StringField("ticket", index.Ticket, Field.Store.NO),
            new SortedDocValuesField("ticket", new BytesRef(index.Ticket)),
            new DoubleField("min_count", index.MinMaxLadder[^1].Min, Field.Store.NO),
            new DoubleField("max_count", index.MinMaxLadder[0].Max, Field.Store.NO),
            new StringField("party_id", index.PartyId, Field.Store.NO),
            new NumericDocValuesField("created_at", index.CreatedAt),
        };

        foreach (var (key, value) in index.Properties)
        {
            string path = "properties." + key;
            switch (value)
            {
                case null:
                    continue;
                case string s:
                    doc.Add(new StringField(path, s, Field.Store.NO));
                    continue;
                case bool b:
                    doc.Add(new StringField(path, b ? "T" : "F", Field.Store.NO));
                    continue;
                case double or float or int or long or short or byte or uint or ulong or ushort or sbyte or decimal:
                    doc.Add(new DoubleField(path, Convert.ToDouble(value), Field.Store.NO));
                    continue;
                default:
                    throw new ArgumentException($"unsupported matchmaker property type {value.GetType().Name} at {path}; " + "properties must be strings or numbers");
            }
        }

        _writer.UpdateDocument(new Term("ticket", index.Ticket), doc);
    }

    public void Delete(string ticket)
    {
        _writer.DeleteDocuments(new Term("ticket", ticket));
    }

    public void DeleteBatch(IEnumerable<string> tickets)
    {
        foreach (var ticket in tickets)
        {
            _writer.DeleteDocuments(new Term("ticket", ticket));
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
        _directory.Dispose();
    }
}
